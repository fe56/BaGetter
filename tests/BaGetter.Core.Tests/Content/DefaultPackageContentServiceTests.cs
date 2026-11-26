using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NuGet.Versioning;
using Xunit;

namespace BaGetter.Core.Tests.Content;

public class DefaultPackageContentServiceTests
{
    private const string PackageId = "Package";
    private static readonly NuGetVersion PackageVersion = new("1.0.0");

    private readonly Mock<IPackageService> _packages = new();
    private readonly Mock<IPackageStorageService> _storage = new();
    private readonly DefaultPackageContentService _target;

    public DefaultPackageContentServiceTests()
    {
        _packages
            .Setup(p => p.ExistsAsync(PackageId, PackageVersion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _target = new DefaultPackageContentService(_packages.Object, _storage.Object);
    }

    [Fact]
    public async Task ReturnsPackageStream()
    {
        using var stream = new MemoryStream();
        _storage
            .Setup(s => s.GetPackageStreamAsync(PackageId, PackageVersion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stream);

        var result = await _target.GetPackageContentStreamOrNullAsync(PackageId, PackageVersion);

        Assert.Same(stream, result);
    }

    [Fact]
    public async Task ReturnsNullWhenPackageFileWasDeleted()
    {
        // The package may be removed from storage (e.g. by a retention cleanup) after the database lookup.
        _storage
            .Setup(s => s.GetPackageStreamAsync(PackageId, PackageVersion, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException());

        var result = await _target.GetPackageContentStreamOrNullAsync(PackageId, PackageVersion);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNullWhenNuspecDirectoryWasDeleted()
    {
        _storage
            .Setup(s => s.GetNuspecStreamAsync(PackageId, PackageVersion, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DirectoryNotFoundException());

        var result = await _target.GetPackageManifestStreamOrNullAsync(PackageId, PackageVersion);

        Assert.Null(result);
    }
}
