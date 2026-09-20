using System.Text.Json;
using AssetsTools.NET;

namespace DorkDl.Core.Unity;

// Serializes an asset's field tree as JSON (used for MonoBehaviour assets).
// Folds Unity's Array/size wrappers, keeps PPtrs as m_FileID/m_PathID, and dumps byte arrays as text or base64.
public static class FieldJson
{
    public static void Write(Stream output, AssetTypeValueField root)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        WriteValue(writer, root);
    }

    private static void WriteValue(Utf8JsonWriter writer, AssetTypeValueField field)
    {
        var template = field.TemplateField;

        if (template.IsArray)
        {
            WriteArray(writer, field);
            return;
        }

        // Strings are char arrays collapsed by the reader; check scalar value before inspecting children.
        var value = field.Value;
        if (value is not null && value.ValueType != AssetValueType.None)
        {
            WriteScalar(writer, value);
            return;
        }

        // Flatten Unity's {"Array": [...]} wrapper struct into a clean JSON array.
        if (field.Children.Count == 1 && field.Children[0].TemplateField.IsArray)
        {
            WriteArray(writer, field.Children[0]);
            return;
        }

        writer.WriteStartObject();
        foreach (var child in field.Children)
        {
            writer.WritePropertyName(child.FieldName);
            WriteValue(writer, child);
        }

        writer.WriteEndObject();
    }

    private static void WriteArray(Utf8JsonWriter writer, AssetTypeValueField array)
    {
        // Byte arrays are read whole rather than as individual elements.
        if (array.Value?.ValueType == AssetValueType.ByteArray)
        {
            WriteScalar(writer, array.Value);
            return;
        }

        writer.WriteStartArray();
        foreach (var element in array.Children)
        {
            WriteValue(writer, element);
        }

        writer.WriteEndArray();
    }

    private static void WriteScalar(Utf8JsonWriter writer, AssetTypeValue value)
    {
        switch (value.ValueType)
        {
            case AssetValueType.Bool:
                writer.WriteBooleanValue(value.AsBool);
                break;
            case AssetValueType.Int8:
            case AssetValueType.Int16:
            case AssetValueType.Int32:
                writer.WriteNumberValue(value.AsInt);
                break;
            case AssetValueType.UInt8:
            case AssetValueType.UInt16:
            case AssetValueType.UInt32:
                writer.WriteNumberValue(value.AsUInt);
                break;
            case AssetValueType.Int64:
                writer.WriteNumberValue(value.AsLong);
                break;
            case AssetValueType.UInt64:
                writer.WriteNumberValue(value.AsULong);
                break;
            case AssetValueType.Float:
                WriteFloat(writer, value.AsFloat);
                break;
            case AssetValueType.Double:
                WriteFloat(writer, value.AsDouble);
                break;
            case AssetValueType.String:
                writer.WriteStringValue(value.AsString);
                break;
            case AssetValueType.ByteArray:
                var bytes = value.AsByteArray;
                if (AssetConverter.LooksLikeText(bytes))
                {
                    writer.WriteStringValue(System.Text.Encoding.UTF8.GetString(bytes));
                }
                else
                {
                    writer.WriteBase64StringValue(bytes);
                }

                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    // Serializes NaN / Infinity as strings to avoid JSON formatting errors.
    private static void WriteFloat(Utf8JsonWriter writer, double value)
    {
        if (double.IsFinite(value))
        {
            writer.WriteNumberValue(value);
        }
        else
        {
            writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
