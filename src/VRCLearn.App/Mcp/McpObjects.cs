using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace VRCLearn.App.Mcp;

/// <summary>引用保持对象身份, 不按列表下标定位可变项目。</summary>
public sealed class McpObjects
{
    private readonly ConditionalWeakTable<object, ObjectId> _ids = new();
    private readonly Dictionary<string, WeakReference<object>> _objects = [];
    private int _nextId;

    private sealed record ObjectId(string Value);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string Reference(object value)
    {
        var id = _ids.GetValue(value, _ => new ObjectId($"object-{++_nextId}"));
        _objects[id.Value] = new WeakReference<object>(value);
        return id.Value;
    }

    public object Resolve(string reference) =>
        _objects.TryGetValue(reference, out var weak) && weak.TryGetTarget(out var value)
            ? value
            : throw new ArgumentException($"对象引用已失效或不存在: {reference}. 请重新读取页面。");

    public object? Value(object? value)
    {
        if (value is null || IsScalar(value.GetType()))
        {
            return value is Enum ? value.ToString() : value;
        }

        if (value.GetType().IsValueType)
        {
            return value.ToString();
        }

        if (value is Type or MemberInfo or Delegate or Task or IServiceProvider)
        {
            return null;
        }

        return new { reference = Reference(value), type = value.GetType().Name };
    }

    public object Inspect(object target, string? property = null, int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "offset 必须 >= 0, limit 必须为 1..200。");
        }

        if (property is not null)
        {
            var value = Property(target, property).GetValue(target);
            if (value is null || IsScalar(value.GetType()) || value.GetType().IsValueType)
            {
                return new { value = Value(value) };
            }

            target = value;
        }

        if (target is IEnumerable sequence and not string)
        {
            var page = sequence.Cast<object?>().Skip(offset).Take(limit + 1).ToList();
            return new
            {
                reference = Reference(target),
                offset,
                total = target is ICollection collection ? (int?)collection.Count : null,
                hasMore = page.Count > limit,
                items = page.Take(limit).Select(item => item is null || IsScalar(item.GetType())
                    ? Value(item)
                    : Describe(item)).ToList(),
            };
        }

        return Describe(target);
    }

    public object Describe(object target)
    {
        var properties = new Dictionary<string, object?>();
        var commands = new List<object>();

        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(target);
            }
            catch (TargetInvocationException exception)
            {
                properties[property.Name] = new { error = exception.InnerException?.Message ?? exception.Message };
                continue;
            }

            if (value is ICommand command)
            {
                var parameterType = ParameterType(command);
                commands.Add(new
                {
                    name = property.Name,
                    parameterType = parameterType?.Name,
                    canExecuteWithoutParameter = parameterType is null ? command.CanExecute(null) : (bool?)null,
                    isAsync = command is IAsyncRelayCommand,
                });
                continue;
            }

            properties[property.Name] = new
            {
                type = property.PropertyType.Name,
                writable = property.SetMethod?.IsPublic == true,
                value = Value(value),
            };
        }

        return new { reference = Reference(target), type = target.GetType().Name, properties, commands };
    }

    public void Set(object target, string propertyName, JsonElement value)
    {
        var property = Property(target, propertyName);
        if (property.SetMethod?.IsPublic != true || typeof(ICommand).IsAssignableFrom(property.PropertyType))
        {
            throw new ArgumentException($"属性不可写: {propertyName}");
        }

        property.SetValue(target, Convert(value, property.PropertyType));
    }

    public (ICommand Command, object? Parameter) Command(object target, string name, JsonElement? argument)
    {
        if (Property(target, name).GetValue(target) is not ICommand command)
        {
            throw new ArgumentException($"不是命令: {name}");
        }

        var parameterType = ParameterType(command);
        if (parameterType is null && argument is { ValueKind: not JsonValueKind.Null })
        {
            throw new ArgumentException($"命令不接受参数: {name}");
        }

        var parameter = parameterType is not null && argument is { } json
            ? Convert(json, parameterType)
            : null;

        if (!command.CanExecute(parameter))
        {
            throw new InvalidOperationException($"命令当前不可用: {name}");
        }

        return (command, parameter);
    }

    public object? Convert(JsonElement value, Type type)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("reference", out var reference))
        {
            var target = Resolve(reference.GetString() ?? "");
            return type.IsInstanceOfType(target)
                ? target
                : throw new ArgumentException($"引用类型 {target.GetType().Name} 与 {type.Name} 不匹配。");
        }

        if (value.ValueKind == JsonValueKind.Null && type.IsValueType && Nullable.GetUnderlyingType(type) is null)
        {
            throw new ArgumentException($"{type.Name} 不接受 null。");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!IsScalar(Nullable.GetUnderlyingType(type) ?? type) && type != typeof(string[]))
        {
            throw new ArgumentException($"{type.Name} 需要对象 reference, 请从 inspect 结果中选择。");
        }

        return JsonSerializer.Deserialize(value.GetRawText(), type, JsonOptions);
    }

    public static Type? ParameterType(ICommand command) => command.GetType().GetInterfaces()
        .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRelayCommand<>))
        ?.GetGenericArguments()[0];

    private static PropertyInfo Property(object target, string name)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0)
        {
            throw new ArgumentException($"公开属性不存在: {name}");
        }

        return property;
    }

    private static bool IsScalar(Type type) => type.IsPrimitive || type.IsEnum
        || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
        || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid);
}
