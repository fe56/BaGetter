using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NuGet.Versioning;

namespace BaGetter.Core;

public class PackageDatabase : IPackageDatabase
{
    private readonly IContext _context;

    public PackageDatabase(IContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<PackageAddResult> AddAsync(Package package, CancellationToken cancellationToken)
    {
        try
        {
            _context.Packages.Add(package);

            await _context.SaveChangesAsync(cancellationToken);

            return PackageAddResult.Success;
        }
        catch (DbUpdateException e)
            when (_context.IsUniqueConstraintViolationException(e))
        {
            return PackageAddResult.PackageAlreadyExists;
        }
    }

    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken)
    {
        return await _context
            .Packages
            .Where(p => p.Id == id)
            .AnyAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        return await _context
            .Packages
            .Where(p => p.Id == id)
            .Where(p => p.NormalizedVersionString == version.ToNormalizedString())
            .AnyAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Package>> FindAsync(string id, bool includeUnlisted, CancellationToken cancellationToken)
    {
        var query = _context.Packages
            .Include(p => p.Dependencies)
            .Include(p => p.PackageTypes)
            .Include(p => p.TargetFrameworks)
            .Where(p => p.Id == id);

        if (!includeUnlisted)
        {
            query = query.Where(p => p.Listed);
        }

        return (await query.AsSingleQuery().ToListAsync(cancellationToken)).AsReadOnly();
    }

    public Task<Package> FindOrNullAsync(
        string id,
        NuGetVersion version,
        bool includeUnlisted,
        CancellationToken cancellationToken)
    {
        var query = _context.Packages
            .Include(p => p.Dependencies)
            .Include(p => p.TargetFrameworks)
            .Where(p => p.Id == id)
            .Where(p => p.NormalizedVersionString == version.ToNormalizedString());

        if (!includeUnlisted)
        {
            query = query.Where(p => p.Listed);
        }

        return query.AsSingleQuery().FirstOrDefaultAsync(cancellationToken);
    }

    public Task<bool> UnlistPackageAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        return TryUpdatePackageAsync(id, version, p => p.Listed = false, cancellationToken);
    }

    public Task<bool> RelistPackageAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        return TryUpdatePackageAsync(id, version, p => p.Listed = true, cancellationToken);
    }

    public async Task AddDownloadAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        await TryUpdatePackageAsync(id, version, p => p.Downloads += 1, cancellationToken);
    }

    public async Task<bool> HardDeletePackageAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        var package = await _context.Packages
            .Where(p => p.Id == id)
            .Where(p => p.NormalizedVersionString == version.ToNormalizedString())
            .Include(p => p.Dependencies)
            .Include(p => p.TargetFrameworks)
            .AsSingleQuery()
            .FirstOrDefaultAsync(cancellationToken);

        if (package == null)
        {
            return false;
        }

        _context.Packages.Remove(package);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException e)
        {
            // The package was deleted concurrently (e.g. by a parallel retention cleanup).
            Detach(package, e);
            return false;
        }

        return true;
    }

    private async Task<bool> TryUpdatePackageAsync(
        string id,
        NuGetVersion version,
        Action<Package> action,
        CancellationToken cancellationToken)
    {
        var package = await _context.Packages
            .Where(p => p.Id == id)
            .Where(p => p.NormalizedVersionString == version.ToNormalizedString())
            .FirstOrDefaultAsync(cancellationToken);

        if (package == null)
        {
            return false;
        }

        action(package);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException e)
        {
            // The package was deleted concurrently (e.g. by a retention cleanup) after it was loaded.
            Detach(package, e);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stops tracking a package whose save failed so that the failed change
    /// is not retried by later <see cref="IContext.SaveChangesAsync"/> calls on the same context.
    /// </summary>
    private void Detach(Package package, DbUpdateConcurrencyException exception)
    {
        foreach (var entry in exception.Entries)
        {
            entry.State = EntityState.Detached;
        }

        var packageEntry = _context.Packages.Entry(package);
        var context = packageEntry.Context;

        foreach (var dependency in package.Dependencies ?? [])
        {
            context.Entry(dependency).State = EntityState.Detached;
        }

        foreach (var targetFramework in package.TargetFrameworks ?? [])
        {
            context.Entry(targetFramework).State = EntityState.Detached;
        }

        packageEntry.State = EntityState.Detached;
    }
}
