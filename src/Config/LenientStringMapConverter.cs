using System.Text.Json;
using System.Text.Json.Serialization;

namespace XiControl.Config;

/// <summary>
/// Словарь «строка → строка», терпимый к тому, как человек правит config.json руками (XIC-38).
/// Коды клавиш пишут и как <c>"0x18"</c>, и как <c>24</c> без кавычек — по умолчанию
/// System.Text.Json на числе бросает исключение, а его ловит <see cref="JsonConfigStore.Load"/>
/// и откатывает ВЕСЬ конфиг к дефолтам: одна забытая кавычка стирала бы человеку все настройки.
/// Поэтому число, true/false и null читаются как строки, а совсем неожиданные значения
/// (объект, массив) — пропускаются молча.
/// </summary>
public sealed class LenientStringMapConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return null; }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return map;
            if (reader.TokenType != JsonTokenType.PropertyName) { reader.Skip(); continue; }

            string key = reader.GetString() ?? "";
            reader.Read();
            switch (reader.TokenType)
            {
                case JsonTokenType.String: map[key] = reader.GetString() ?? ""; break;
                case JsonTokenType.Number: map[key] = reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                case JsonTokenType.True: map[key] = "true"; break;
                case JsonTokenType.False: map[key] = "false"; break;
                case JsonTokenType.Null: break; // «сбросить слот» — просто нет записи
                default: reader.Skip(); break;  // объект/массив здесь бессмысленны
            }
        }
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value) writer.WriteString(k, v);
        writer.WriteEndObject();
    }
}
