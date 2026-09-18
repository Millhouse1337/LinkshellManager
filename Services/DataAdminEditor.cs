using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LinkshellManagerDiscordApp.Services;

public sealed record DataAdminFieldError(string Field, string Message);

public sealed class DataAdminEditResult
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
    public object? Entity { get; init; }
    public List<DataAdminFieldError> Errors { get; } = new();
    public bool Succeeded => !Forbidden && !NotFound && Errors.Count == 0;
}

// Create and edit for any catalog table. The form posts a plain name -> text dictionary; this
// binds it against the table's EDITABLE set only (a posted key outside that set is ignored, never
// written), converts and validates every value through DataAdminValues, keeps denormalised copies
// in step, runs the policy's BeforeSave hook, and turns a database refusal into a message on the
// form instead of a 500. Every successful write is logged with who, what and which fields.
public sealed class DataAdminEditor
{
    private readonly ApplicationDbContext _db;
    private readonly GlobalSettingsService _settings;
    private readonly ILogger<DataAdminEditor> _logger;

    public DataAdminEditor(ApplicationDbContext db, GlobalSettingsService settings, ILogger<DataAdminEditor> logger)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public async Task<DataAdminEditResult> CreateAsync(DataAdminModel table, IReadOnlyDictionary<string, string?> values, AppUser actor, CancellationToken ct)
    {
        if (!table.CanCreate)
        {
            return new DataAdminEditResult { Forbidden = true };
        }

        var entity = table.CreateInstance();
        var result = new DataAdminEditResult { Entity = entity };
        Bind(table.CreateColumns, entity, values, result);
        if (result.Errors.Count > 0)
        {
            return result;
        }

        await SyncCopiesAsync(table, entity, ct);
        if (table.Policy.BeforeSave is { } hook)
        {
            await hook(_db, entity, ct);
        }
        _db.Add(entity);
        return await SaveAsync(table, entity, result, actor, "created", table.CreateColumns.Select(c => c.Name).ToList(), ct);
    }

    public async Task<DataAdminEditResult> UpdateAsync(DataAdminModel table, object key, IReadOnlyDictionary<string, string?> values, AppUser actor, CancellationToken ct)
    {
        if (!table.CanEdit)
        {
            return new DataAdminEditResult { Forbidden = true };
        }
        var entity = await table.FindAsync(_db, key, track: true, ct);
        if (entity is null)
        {
            return new DataAdminEditResult { NotFound = true };
        }

        var result = new DataAdminEditResult { Entity = entity };
        Bind(table.EditableColumns, entity, values, result);
        if (result.Errors.Count > 0)
        {
            return result;
        }

        await SyncCopiesAsync(table, entity, ct);
        if (table.Policy.BeforeSave is { } hook)
        {
            await hook(_db, entity, ct);
        }
        var changed = _db.Entry(entity).Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToList();
        return await SaveAsync(table, entity, result, actor, "updated", changed, ct);
    }

    // The form for a table: one field per create/edit column, prefilled from the entity (edit) or
    // from the posted values (a re-render after errors). Foreign keys become a <select> of the
    // principal's labels while that table is small enough; beyond that, the raw key is typed in.
    public async Task<DataAdminEditViewModel> BuildFormAsync(
        DataAdminModel table,
        object? entity,
        IReadOnlyDictionary<string, string?>? posted,
        IReadOnlyList<DataAdminFieldError> errors,
        CancellationToken ct)
    {
        var isCreate = entity is null;
        var model = new DataAdminEditViewModel
        {
            Slug = table.Slug,
            DisplayName = table.DisplayName,
            IsCreate = isCreate,
            Key = entity is null ? null : table.KeyToString(table.KeyOf(entity)),
            Label = entity is null ? null : table.LabelOf(entity),
            DiscordSideEffect = table.Policy.DiscordSideEffect,
        };
        model.FormErrors.AddRange(errors.Where(e => e.Field.Length == 0).Select(e => e.Message));

        // A create form starts from a fresh instance so the entity's own defaults (timestamps,
        // flags, empty strings) are what the admin sees and submits.
        var source = entity ?? table.CreateInstance();
        foreach (var column in isCreate ? table.CreateColumns : table.EditableColumns)
        {
            string value;
            if (posted is not null && posted.TryGetValue(column.Name, out var postedValue))
            {
                value = postedValue ?? string.Empty;
            }
            else
            {
                value = DataAdminFormat.ToInputValue(column.Property.GetValue(source));
            }

            var field = new DataAdminFieldViewModel
            {
                Name = column.Name,
                DisplayName = column.DisplayName,
                Value = value,
                InputType = InputTypeFor(column),
                IsRequired = !column.IsNullable,
                MaxLength = column.MaxLength,
                IsKey = column.IsKey,
                Error = errors.FirstOrDefault(e => e.Field == column.Name)?.Message,
            };

            if (column.Options is { Count: > 0 } options)
            {
                field.InputType = "select";
                field.Options = options.Select(o => new DataAdminSelectOption(o, o)).ToList();
            }
            else if (column.ForeignKeyTo is { } principal)
            {
                var count = await principal.CountAsync(_db, ct);
                if (count <= DataAdminDefaults.MaxSelectRows)
                {
                    field.InputType = "select";
                    field.Options = (await principal.ListLabelsAsync(_db, DataAdminDefaults.MaxSelectRows, ct))
                        .Select(pair => new DataAdminSelectOption(pair.Key, $"{pair.Value} (#{pair.Key})"))
                        .ToList();
                }
                else
                {
                    field.Hint = $"{principal.DisplayName} key ({count:N0} rows, too many for a list)";
                }
            }
            model.Fields.Add(field);
        }
        return model;
    }

    // Postgres error codes -> a sentence the form can show. Anything else keeps the driver's text.
    public static string DescribeSaveFailure(DbUpdateException exception)
    {
        if (exception.InnerException is PostgresException pg)
        {
            var constraint = string.IsNullOrEmpty(pg.ConstraintName) ? string.Empty : $" ({pg.ConstraintName})";
            return pg.SqlState switch
            {
                PostgresErrorCodes.UniqueViolation => $"A row with the same unique value already exists{constraint}.",
                PostgresErrorCodes.CheckViolation => $"The database rejected these values{constraint}.",
                PostgresErrorCodes.ForeignKeyViolation => $"A referenced row does not exist, or other rows still reference this one{constraint}.",
                PostgresErrorCodes.NotNullViolation => $"{pg.ColumnName ?? "A required column"} cannot be empty.",
                _ => pg.MessageText,
            };
        }
        return exception.GetBaseException().Message;
    }

    private static void Bind(IReadOnlyList<DataAdminColumn> columns, object entity, IReadOnlyDictionary<string, string?> values, DataAdminEditResult result)
    {
        foreach (var column in columns)
        {
            // A field the form did not post keeps what the row (or, on create, the entity's own
            // initialiser -- CreatedAt = DateTime.UtcNow) already holds. A posted blank on a
            // required column is the error case.
            if (!values.TryGetValue(column.Name, out var raw))
            {
                continue;
            }
            if (!DataAdminValues.TryConvert(raw, column, out var value, out var error))
            {
                result.Errors.Add(new DataAdminFieldError(column.Name, error));
                continue;
            }
            column.Property.SetValue(entity, value);
        }
    }

    // Denormalised copies (Rule.LinkshellName <- Linkshell.LinkshellName) follow the foreign key.
    private async Task SyncCopiesAsync(DataAdminModel table, object entity, CancellationToken ct)
    {
        foreach (var copy in table.Policy.SyncedCopies)
        {
            var via = table.FindColumn(copy.ViaForeignKey);
            var target = table.FindColumn(copy.TargetColumn);
            var principal = via?.ForeignKeyTo;
            var sourceColumn = principal?.FindColumn(copy.SourceColumn);
            if (via is null || target is null || principal is null || sourceColumn is null)
            {
                continue;
            }
            var key = via.Property.GetValue(entity);
            var source = key is null ? null : await principal.FindAsync(_db, key, track: false, ct);
            var copied = source is null ? null : sourceColumn.Property.GetValue(source);
            target.Property.SetValue(entity, copied ?? (target.IsNullable ? null : string.Empty));
        }
    }

    private async Task<DataAdminEditResult> SaveAsync(DataAdminModel table, object entity, DataAdminEditResult result, AppUser actor, string verb, IReadOnlyList<string> fields, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception)
        {
            result.Errors.Add(new DataAdminFieldError(string.Empty, DescribeSaveFailure(exception)));
            return result;
        }

        // GlobalSettingsService caches AppSettings reads for 30s; a setting edited here should apply at once.
        if (entity is AppSetting setting)
        {
            _settings.Invalidate(setting.Key);
        }

        _logger.LogWarning(
            "Data Admin: {Actor} ({ActorId}) {Verb} {Table} #{Key} [{Fields}]",
            actor.UserName,
            actor.Id,
            verb,
            table.ClrName,
            table.KeyToString(table.KeyOf(entity)),
            string.Join(", ", fields));
        return result;
    }

    private static string InputTypeFor(DataAdminColumn column)
    {
        var type = column.UnderlyingType;
        if (type == typeof(bool))
        {
            return "checkbox";
        }
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return "datetime-local";
        }
        if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal))
        {
            return "number";
        }
        if (type == typeof(string) && (column.MaxLength is null || column.MaxLength > 200))
        {
            return "textarea";
        }
        return "text";
    }
}
