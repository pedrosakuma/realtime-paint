using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocialPresence.RealtimePaint;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StrokeEvent))]
internal partial class PaintJsonContext : JsonSerializerContext;
