using System.Text.Json;

namespace Warp.Core.Handlers;

public static class MetadataConvert
{
    public static T? To<T>(object? value)
    {
        if (value == null)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        if (value is JsonElement element)
        {
            try
            {
                return element.Deserialize<T>();
            }
            catch (JsonException)
            {
                return default;
            }
        }

        // Numeric conversions (NativeObjectConverter returns long for all integers)
        if (typeof(T) == typeof(int) || typeof(T) == typeof(int?))
        {
            return (T)(object)Convert.ToInt32(value);
        }

        if (typeof(T) == typeof(long) || typeof(T) == typeof(long?))
        {
            return (T)(object)Convert.ToInt64(value);
        }

        if (typeof(T) == typeof(double) || typeof(T) == typeof(double?))
        {
            return (T)(object)Convert.ToDouble(value);
        }

        // Enum / Nullable<Enum>. After enums-as-strings (2026-05-25), persisted metadata
        // carries the enum name as a JSON string, which NativeObjectConverter returns as
        // string. Try name parse first; fall back to integer-value conversion for any
        // path that still hands us a long (e.g. an in-memory dict constructed by hand or
        // a future deserializer change).
        var underlying = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (underlying.IsEnum)
        {
            if (value is string s && Enum.TryParse(underlying, s, ignoreCase: true, out var parsed))
            {
                return (T)parsed;
            }

            try
            {
                return (T)Enum.ToObject(underlying, value);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidCastException or OverflowException)
            {
                return default;
            }
        }

        // DateTime / Nullable<DateTime> ← string (NativeObjectConverter returns string for ISO
        // 8601 timestamps written by JsonSerializer.Serialize). Round-trip via DateTimeStyles
        // RoundtripKind to preserve the original Kind (UTC).
        if (underlying == typeof(DateTime) && value is string dateString)
        {
            if (DateTime.TryParse(
                dateString,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var dt))
            {
                return (T)(object)dt;
            }

            return default;
        }

        // List<object> → T[] conversion (NativeObjectConverter returns List<object> for arrays)
        if (typeof(T).IsArray && value is System.Collections.IList list)
        {
            var elementType = typeof(T).GetElementType()!;
            var array = Array.CreateInstance(elementType, list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                array.SetValue(Convert.ChangeType(list[i], elementType), i);
            }

            return (T)(object)array;
        }

        return default;
    }
}
