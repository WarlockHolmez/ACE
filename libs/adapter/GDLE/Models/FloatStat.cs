using System.Text.Json.Serialization;
using Lifestoned.DataModel.Shared;

namespace ACE.Adapter.GDLE.Models;

public class FloatStat
{
    [JsonPropertyName("key")]
    public int Key { get; set; }

    [JsonPropertyName("value")]
    public float Value { get; set; }

    [JsonIgnore]
    public string PropertyIdBinder => ((DoublePropertyId)Key).GetName();

    [JsonIgnore]
    public bool Deleted { get; set; }
}
