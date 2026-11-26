using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BaGetter.Core;
using BaGetter.Database.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Moq;
using NuGet.Versioning;
using Xunit;

namespace BaGetter.Tests;

/// <summary>
/// Simulates a package being deleted by another request (e.g. a parallel retention cleanup)
/// between <see cref="PackageDatabase"/> loading it and saving changes to it.
/// </summary>
public class PackageDatabaseConcurrencyTests : IAsyncLifetime
{
    private static readonly NuGetVersion Version = new("1.0.0");

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"BaGetter.{Guid.NewGuid():N}.db");
    private readonly DeleteBeforeSaveInterceptor _interceptor = new();

    private SqliteContext _context;
    private PackageDatabase _target;

    public async Task InitializeAsync()
    {
        var efOptions = new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(_interceptor)
            .Options;

        var bagetterOptions = new Mock<IOptionsSnapshot<BaGetterOptions>>();
        bagetterOptions.Setup(o => o.Value).Returns(new BaGetterOptions { Database = new DatabaseOptions() });

        _context = new SqliteContext(efOptions, bagetterOptions.Object);
        await _context.Database.MigrateAsync();

        _target = new PackageDatabase(_context);

        await _target.AddAsync(CreatePackage("Deleted.Package"), CancellationToken.None);
        await _target.AddAsync(CreatePackage("Other.Package"), CancellationToken.None);

        _context.ChangeTracker.Clear();
        _interceptor.Configure(_databasePath, "Deleted.Package");
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task AddDownloadAsync_DoesNotThrowWhenPackageWasDeletedConcurrently()
    {
        await _target.AddDownloadAsync("Deleted.Package", Version, CancellationToken.None);

        Assert.True(_interceptor.Deleted);
        Assert.False(await _target.ExistsAsync("Deleted.Package", Version, CancellationToken.None));

        // The failed change must not be retried by later saves on the same context.
        await _target.AddDownloadAsync("Other.Package", Version, CancellationToken.None);

        var other = await _target.FindOrNullAsync("Other.Package", Version, includeUnlisted: true, CancellationToken.None);
        Assert.Equal(1, other.Downloads);
    }

    [Fact]
    public async Task HardDeletePackageAsync_ReturnsFalseWhenPackageWasDeletedConcurrently()
    {
        var result = await _target.HardDeletePackageAsync("Deleted.Package", Version, CancellationToken.None);

        Assert.True(_interceptor.Deleted);
        Assert.False(result);

        // The failed change must not be retried by later saves on the same context.
        Assert.True(await _target.HardDeletePackageAsync("Other.Package", Version, CancellationToken.None));
        Assert.False(await _target.ExistsAsync("Other.Package", Version, CancellationToken.None));
    }

    private static Package CreatePackage(string id) => new()
    {
        Id = id,
        Version = Version,
        Authors = ["Author"],
        Description = "Description",
        Listed = true,
        Published = DateTime.UtcNow,
        Tags = [],
        Dependencies = [new PackageDependency { Id = "Dependency", VersionRange = "[1.0.0, )", TargetFramework = "net9.0" }],
        PackageTypes = [new PackageType { Name = "Dependency", Version = "0.0" }],
        TargetFrameworks = [new TargetFramework { Moniker = "net9.0" }],
    };

    /// <summary>
    /// Deletes a package through a separate connection right before the next save is executed.
    /// </summary>
    private sealed class DeleteBeforeSaveInterceptor : SaveChangesInterceptor
    {
        private string _databasePath;
        private string _packageId;

        public bool Deleted { get; private set; }

        public void Configure(string databasePath, string packageId)
        {
            _databasePath = databasePath;
            _packageId = packageId;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_packageId != null && !Deleted)
            {
                await using var connection = new SqliteConnection($"Data Source={_databasePath}");
                await connection.OpenAsync(cancellationToken);

                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA foreign_keys = ON;
                    DELETE FROM "PackageDependencies" WHERE "PackageKey" IN (SELECT "Key" FROM "Packages" WHERE "Id" = $id);
                    DELETE FROM "Packages" WHERE "Id" = $id;
                    """;
                command.Parameters.AddWithValue("$id", _packageId);
                await command.ExecuteNonQueryAsync(cancellationToken);

                Deleted = true;
            }

            return result;
        }
    }
}
