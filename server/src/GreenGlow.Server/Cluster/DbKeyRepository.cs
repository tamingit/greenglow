using System.Xml.Linq;
using GreenGlow.Server.Data;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GreenGlow.Server.Cluster;

/// <summary>
/// The sign-in cookies' Data Protection key ring, kept in the database rather than in files so it
/// syncs: every node reads every node's cookies.
/// </summary>
public sealed class DbKeyRepository(IDbContextFactory<GreenGlowDb> dbs) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var db = dbs.CreateDbContext();
        return db.SigningKeys.AsNoTracking().Select(k => k.Xml).ToList().Select(x => XElement.Parse(x)).ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var db = dbs.CreateDbContext();
        var name = string.IsNullOrEmpty(friendlyName) ? Guid.NewGuid().ToString() : friendlyName;
        var row = db.SigningKeys.Find(name);
        if (row is null) db.SigningKeys.Add(new SigningKey { Name = name, Xml = element.ToString(SaveOptions.DisableFormatting) });
        else row.Xml = element.ToString(SaveOptions.DisableFormatting);
        db.SaveChanges();
    }

    /// <summary>Keys an earlier version kept as files in a folder (keys, beside the database): added once.</summary>
    public static async Task<int> ImportAsync(IDbContextFactory<GreenGlowDb> dbs, string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        await using var db = await dbs.CreateDbContextAsync();
        var known = (await db.SigningKeys.Select(k => k.Name).ToListAsync()).ToHashSet();
        var added = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.xml"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!known.Add(name)) continue;
            try
            {
                var xml = XElement.Load(file);
                db.SigningKeys.Add(new SigningKey { Name = name, Xml = xml.ToString(SaveOptions.DisableFormatting) });
                added++;
            }
            catch (System.Xml.XmlException)
            {
                // Not a key file.
            }
        }
        await db.SaveChangesAsync();
        return added;
    }
}
