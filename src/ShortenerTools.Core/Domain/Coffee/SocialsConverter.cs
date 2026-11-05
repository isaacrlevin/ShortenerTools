using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ShortenerTools.Core.Domain.Coffee;

public sealed class SocialsConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string> ReadJson(
        JsonReader reader, Type objectType, Dictionary<string, string>? existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        var socials = new Dictionary<string, string>();
        foreach (JObject social in JArray.Load(reader))
        {
            foreach (var property in social.Properties())
            {
                socials[property.Name] = property.Value.ToString();
            }
        }
        return socials;
    }

    public override void WriteJson(JsonWriter writer, Dictionary<string, string>? value, JsonSerializer serializer)
    {
        writer.WriteStartArray();
        if (value is not null)
        {
            foreach (var (name, handle) in value)
            {
                writer.WriteStartObject();
                writer.WritePropertyName(name);
                writer.WriteValue(handle);
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
    }
}
