using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using GreenGlow.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GreenGlow.Server.Cluster;

/// <summary>
/// The tables nodes sync, from the EF model: each row's key as text, its columns as JSON, and the
/// unique rules and foreign keys that conflict resolution needs. A row's JSON names columns by
/// property, so a node ignores ones it doesn't know and leaves alone ones a peer didn't send.
/// </summary>
public sealed class SyncModel
{
    /// <summary>Everything in the database that isn't local to a node or the connection log.</summary>
    private static readonly Type[] Synced =
    [
        typeof(Setting), typeof(AppUser), typeof(SigningKey), typeof(HostConnection), typeof(ConnectionGrant),
        typeof(UserConnectionColor), typeof(SessionShare), typeof(ShareRecipient), typeof(Macro), typeof(MacroShare),
        typeof(MacroKey),
    ];

    private static readonly Lock CacheLock = new();
    private static (IModel Model, SyncModel Sync)? _cache;

    private readonly Dictionary<string, SyncTable> _byName;
    private readonly Dictionary<Type, SyncTable> _byType;

    private SyncModel(IModel model)
    {
        var tables = Synced.Select(t => new SyncTable(model.FindEntityType(t)!)).ToList();
        _byType = tables.ToDictionary(t => t.Clr);
        foreach (var table in tables) table.Rank = RankOf(table, []);
        Tables = tables.OrderBy(t => t.Rank).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        _byName = Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Logs = new SyncTable(model.FindEntityType(typeof(ConnectionLog))!);
    }

    public static SyncModel For(IModel model)
    {
        lock (CacheLock)
        {
            if (_cache is { } cached && ReferenceEquals(cached.Model, model)) return cached.Sync;
            var sync = new SyncModel(model);
            _cache = (model, sync);
            return sync;
        }
    }

    /// <summary>Parents before children.</summary>
    public IReadOnlyList<SyncTable> Tables { get; }

    /// <summary>The connection log, synced in bulk rather than through the journal.</summary>
    public SyncTable Logs { get; }

    public SyncTable? Find(string name) => _byName.GetValueOrDefault(name);

    public SyncTable? For(Type type) => _byType.GetValueOrDefault(type);

    /// <summary>0 for a table with no parents, else one more than its deepest parent.</summary>
    private int RankOf(SyncTable table, HashSet<Type> seen)
    {
        if (!seen.Add(table.Clr)) return 0;
        var parents = table.Entity.GetForeignKeys().Select(f => For(f.PrincipalEntityType.ClrType)).OfType<SyncTable>()
            .Where(p => p != table).ToList();
        return parents.Count == 0 ? 0 : 1 + parents.Max(p => RankOf(p, seen));
    }
}

/// <summary>One synced table.</summary>
public sealed class SyncTable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    internal SyncTable(IEntityType entity)
    {
        Entity = entity;
        Name = entity.GetTableName()!;
        Key = entity.FindPrimaryKey()!.Properties;
        Properties = entity.GetProperties().Where(p => p.PropertyInfo is not null).ToList();
    }

    public string Name { get; }
    public IEntityType Entity { get; }
    public Type Clr => Entity.ClrType;
    public int Rank { get; internal set; }
    public IReadOnlyList<IProperty> Key { get; }
    public IReadOnlyList<IProperty> Properties { get; }

    public IEnumerable<IIndex> UniqueIndexes => Entity.GetIndexes().Where(i => i.IsUnique);

    /// <summary>Foreign keys from this table to its parents.</summary>
    public IEnumerable<IForeignKey> Parents => Entity.GetForeignKeys();

    /// <summary>Foreign keys from other tables to this one.</summary>
    public IEnumerable<IForeignKey> Children => Entity.GetReferencingForeignKeys();

    /// <summary>The row's key as text: its key columns joined by |.</summary>
    public string KeyOf(object entity) => string.Join('|', Key.Select(p => Format(p.PropertyInfo!.GetValue(entity))));

    public string KeyOf(EntityEntry entry) => KeyOf(entry.Entity);

    /// <summary>A key's text from its columns' values, in key order.</summary>
    public static string KeyText(IEnumerable<object?> values) => string.Join('|', values.Select(Format));

    /// <summary>The key columns' values from the key's text, or null if it doesn't fit this table.</summary>
    public object[]? KeyValues(string key)
    {
        var parts = key.Split('|');
        if (parts.Length != Key.Count) return null;
        var values = new object[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var type = Key[i].ClrType;
            if (type == typeof(Guid))
            {
                if (!Guid.TryParse(parts[i], out var g)) return null;
                values[i] = g;
            }
            else if (type == typeof(string)) values[i] = parts[i];
            else
            {
                try
                {
                    values[i] = Convert.ChangeType(parts[i], type, CultureInfo.InvariantCulture);
                }
                catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
                {
                    return null;
                }
            }
        }
        return values;
    }

    /// <summary>The row's columns, by property name.</summary>
    public JsonObject Serialize(object entity)
    {
        var data = new JsonObject();
        foreach (var p in Properties) data[p.Name] = JsonSerializer.SerializeToNode(p.PropertyInfo!.GetValue(entity), p.ClrType, Json);
        return data;
    }

    /// <summary>A new row from a peer's JSON. Throws JsonException or FormatException if a column doesn't fit.</summary>
    public object Create(JsonObject data)
    {
        var entity = Activator.CreateInstance(Clr)!;
        Fill(entity, data, keys: true);
        return entity;
    }

    /// <summary>
    /// Sets the columns the JSON has; columns it lacks (a peer one version older) keep their
    /// values, and properties this version doesn't have are ignored.
    /// </summary>
    public void Fill(object entity, JsonObject data, bool keys)
    {
        foreach (var p in Properties)
        {
            if (!keys && p.IsPrimaryKey()) continue;
            if (!data.TryGetPropertyValue(p.Name, out var node)) continue;
            p.PropertyInfo!.SetValue(entity, node is null ? null : node.Deserialize(p.ClrType, Json));
        }
    }

    /// <summary>The value of a property of a row, by property.</summary>
    public static object? ValueOf(object entity, IReadOnlyProperty property) => property.PropertyInfo!.GetValue(entity);

    /// <summary>
    /// The rows of a table where each property equals its value, read through EF so that value
    /// conversions and collations (NOCASE names) apply as they do everywhere else.
    /// </summary>
    public static List<object> Where(DbContext db, IEntityType entity, IReadOnlyList<(IReadOnlyProperty Property, object? Value)> equal,
        bool tracked = true)
    {
        var set = (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
            .MakeGenericMethod(entity.ClrType).Invoke(db, null)!;
        if (!tracked)
            set = (IQueryable)typeof(EntityFrameworkQueryableExtensions).GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!
                .MakeGenericMethod(entity.ClrType).Invoke(null, [set])!;
        var row = Expression.Parameter(entity.ClrType, "row");
        Expression? body = null;
        foreach (var (property, value) in equal)
        {
            var column = Expression.Property(row, property.PropertyInfo!);
            var test = Expression.Equal(column, Expression.Constant(value, property.ClrType));
            body = body is null ? test : Expression.AndAlso(body, test);
        }
        var where = Expression.Call(typeof(Queryable), nameof(Queryable.Where), [entity.ClrType], set.Expression,
            Expression.Quote(Expression.Lambda(body ?? Expression.Constant(true), row)));
        return ((System.Collections.IEnumerable)set.Provider.CreateQuery(where)).Cast<object>().ToList();
    }

    /// <summary>Every row of a table, untracked.</summary>
    public List<object> All(DbContext db)
    {
        var set = (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
            .MakeGenericMethod(Clr).Invoke(db, null)!;
        var untracked = typeof(EntityFrameworkQueryableExtensions).GetMethod(nameof(EntityFrameworkQueryableExtensions.AsNoTracking))!
            .MakeGenericMethod(Clr).Invoke(null, [set])!;
        return ((System.Collections.IEnumerable)untracked).Cast<object>().ToList();
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        Guid g => g.ToString("D"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
