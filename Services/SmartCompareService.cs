using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace JsonMaster.Api.Services;

public class SmartCompareService
{
    public async Task<ComparisonResult> CompareJsonsAsync(Stream sourceStream, Stream targetStream, string keyField, string ignoredFields = "")
    {
        var result = new ComparisonResult();
        var ignoredSet = new HashSet<string>(
            ignoredFields.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase
        );

        try
        {
            result.SourceDoc = await JsonDocument.ParseAsync(sourceStream);
            result.TargetDoc = await JsonDocument.ParseAsync(targetStream);

            var source = result.SourceDoc.RootElement;
            var target = result.TargetDoc.RootElement;

            var validation = ValidateKeyField(source, target, keyField);
            if (!validation.IsValid)
            {
                result.ValidationError = validation.ErrorMessage;
                return result;
            }

            if (source.ValueKind == JsonValueKind.Array && target.ValueKind == JsonValueKind.Array)
            {
                CompareArrays(source, target, keyField, ignoredSet, result);
            }
            else if (source.ValueKind == JsonValueKind.Object && target.ValueKind == JsonValueKind.Object)
            {
                if (!string.IsNullOrWhiteSpace(keyField))
                {
                    // Look for array properties in both objects
                    var sourcePropItems = source.EnumerateObject().ToList();
                    var targetPropItems = target.EnumerateObject().ToList();

                    var sourceArrayProp = sourcePropItems.FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array);
                    var targetArrayProp = targetPropItems.FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array);

                    if (sourceArrayProp.Value.ValueKind == JsonValueKind.Array && 
                        targetArrayProp.Value.ValueKind == JsonValueKind.Array && 
                        sourceArrayProp.Name == targetArrayProp.Name)
                    {
                        CompareArrays(sourceArrayProp.Value, targetArrayProp.Value, keyField, ignoredSet, result);
                    }
                    else
                    {
                        result.ValidationError = $"Could not find matching array properties in both objects to compare with keyField '{keyField}'.";
                    }
                }
                else
                {
                    var diff = CompareObjects(source, target, "", ignoredSet);
                    if (diff.Differences.Any())
                    {
                        result.Modified.Add(diff);
                    }
                }
            }
            else
            {
                result.ValidationError = "Both JSONs must be either arrays or objects of the same type.";
            }
        }
        catch (Exception ex)
        {
            result.ValidationError = $"Error parsing JSON: {ex.Message}";
        }

        return result;
    }

    private ValidationResult ValidateKeyField(JsonElement source, JsonElement target, string keyField)
    {
        if (source.ValueKind == JsonValueKind.Object && target.ValueKind == JsonValueKind.Object)
        {
            if (!string.IsNullOrWhiteSpace(keyField))
            {
                var sourceArrayProp = source.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array);
                var targetArrayProp = target.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array);

                if (sourceArrayProp.Name == null || targetArrayProp.Name == null)
                {
                    return new ValidationResult { IsValid = false, ErrorMessage = "Key field provided but no arrays found in the root objects." };
                }

                if (sourceArrayProp.Name != targetArrayProp.Name)
                {
                    return new ValidationResult { IsValid = false, ErrorMessage = $"Array property names don't match: '{sourceArrayProp.Name}' vs '{targetArrayProp.Name}'." };
                }

                if (sourceArrayProp.Value.GetArrayLength() > 0)
                {
                    var first = sourceArrayProp.Value[0];
                    if (first.ValueKind == JsonValueKind.Object && !first.TryGetProperty(keyField, out _))
                        return new ValidationResult { IsValid = false, ErrorMessage = $"Key field '{keyField}' not found in source array '{sourceArrayProp.Name}'." };
                }
            }
            return new ValidationResult { IsValid = true };
        }

        if (source.ValueKind == JsonValueKind.Array && target.ValueKind == JsonValueKind.Array)
        {
            if (string.IsNullOrWhiteSpace(keyField))
            {
                return new ValidationResult { IsValid = false, ErrorMessage = "Key field is required for comparing arrays." };
            }

            if (source.GetArrayLength() > 0)
            {
                var first = source[0];
                if (first.ValueKind == JsonValueKind.Object && !first.TryGetProperty(keyField, out _))
                    return new ValidationResult { IsValid = false, ErrorMessage = $"Key field '{keyField}' not found in source JSON." };
            }
            return new ValidationResult { IsValid = true };
        }

        return new ValidationResult { IsValid = false, ErrorMessage = "Invalid JSON structure." };
    }

    private void CompareArrays(JsonElement source, JsonElement target, string keyField, HashSet<string> ignoredSet, ComparisonResult result)
    {
        var sourceDict = new Dictionary<string, List<JsonElement>>();
        foreach (var item in source.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var key = item.TryGetProperty(keyField, out var val) ? val.ToString() : "";
            if (!sourceDict.ContainsKey(key)) sourceDict[key] = new List<JsonElement>();
            sourceDict[key].Add(item);
        }

        var targetDict = new Dictionary<string, List<JsonElement>>();
        foreach (var item in target.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var key = item.TryGetProperty(keyField, out var val) ? val.ToString() : "";
            if (!targetDict.ContainsKey(key)) targetDict[key] = new List<JsonElement>();
            targetDict[key].Add(item);
        }

        var allKeys = sourceDict.Keys.Union(targetDict.Keys).Distinct();

        foreach (var key in allKeys)
        {
            var sourceItems = sourceDict.GetValueOrDefault(key) ?? new List<JsonElement>();
            var targetItems = targetDict.GetValueOrDefault(key) ?? new List<JsonElement>();

            int count = Math.Max(sourceItems.Count, targetItems.Count);
            for (int i = 0; i < count; i++)
            {
                var src = i < sourceItems.Count ? (JsonElement?)sourceItems[i] : null;
                var tgt = i < targetItems.Count ? (JsonElement?)targetItems[i] : null;

                if (src.HasValue && tgt.HasValue)
                {
                    var diff = CompareObjects(src.Value, tgt.Value, "", ignoredSet);
                    diff.KeyValue = key;
                    if (diff.Differences.Any())
                    {
                        result.Modified.Add(diff);
                    }
                    else
                    {
                        result.Unchanged.Add(src.Value);
                    }
                }
                else if (src.HasValue)
                {
                    result.Removed.Add(src.Value);
                }
                else if (tgt.HasValue)
                {
                    result.Added.Add(tgt.Value);
                }
            }
        }
    }

    private ObjectDiff CompareObjects(JsonElement source, JsonElement target, string basePath, HashSet<string> ignoredSet)
    {
        var diff = new ObjectDiff
        {
            Source = source,
            Target = target,
            Differences = new List<FieldDiff>()
        };

        var sourceProps = source.EnumerateObject().Select(p => p.Name).ToHashSet();
        var targetProps = target.EnumerateObject().Select(p => p.Name).ToHashSet();
        var allKeys = sourceProps.Union(targetProps).Distinct();

        foreach (var key in allKeys)
        {
            if (ignoredSet.Contains(key)) continue;

            var path = string.IsNullOrEmpty(basePath) ? key : $"{basePath}.{key}";
            bool inSource = source.TryGetProperty(key, out var srcVal);
            bool inTarget = target.TryGetProperty(key, out var tgtVal);

            if (inSource && !inTarget)
            {
                diff.Differences.Add(new FieldDiff { Path = path, SourceValue = srcVal, TargetValue = null, ChangeType = "removed" });
            }
            else if (!inSource && inTarget)
            {
                diff.Differences.Add(new FieldDiff { Path = path, SourceValue = null, TargetValue = tgtVal, ChangeType = "added" });
            }
            else if (inSource && inTarget)
            {
                if (srcVal.ValueKind == JsonValueKind.Object && tgtVal.ValueKind == JsonValueKind.Object)
                {
                    var nested = CompareObjects(srcVal, tgtVal, path, ignoredSet);
                    diff.Differences.AddRange(nested.Differences);
                }
                else if (!JsonElementEquals(srcVal, tgtVal))
                {
                    diff.Differences.Add(new FieldDiff { Path = path, SourceValue = srcVal, TargetValue = tgtVal, ChangeType = "modified" });
                }
            }
        }

        return diff;
    }

    private bool JsonElementEquals(JsonElement e1, JsonElement e2)
    {
        if (e1.ValueKind != e2.ValueKind) return false;

        switch (e1.ValueKind)
        {
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                return e1.GetRawText() == e2.GetRawText(); 
            default:
                return e1.GetRawText() == e2.GetRawText();
        }
    }
}

public class ComparisonResult : IDisposable
{
    public JsonDocument? SourceDoc { get; set; }
    public JsonDocument? TargetDoc { get; set; }
    public List<ObjectDiff> Modified { get; set; } = new();
    public List<JsonElement> Added { get; set; } = new();
    public List<JsonElement> Removed { get; set; } = new();
    public List<JsonElement> Unchanged { get; set; } = new();
    public string? ValidationError { get; set; }

    public ComparisonSummary GetSummary()
    {
        return new ComparisonSummary
        {
            TotalRecords = Modified.Count + Added.Count + Removed.Count + Unchanged.Count,
            ModifiedCount = Modified.Count,
            AddedCount = Added.Count,
            RemovedCount = Removed.Count,
            UnchangedCount = Unchanged.Count
        };
    }

    public void Dispose()
    {
        SourceDoc?.Dispose();
        TargetDoc?.Dispose();
    }
}

public class ObjectDiff
{
    public string KeyValue { get; set; } = "";
    public JsonElement? Source { get; set; }
    public JsonElement? Target { get; set; }
    public List<FieldDiff> Differences { get; set; } = new();
}

public class FieldDiff
{
    public string Path { get; set; } = "";
    public object? SourceValue { get; set; }
    public object? TargetValue { get; set; }
    public string ChangeType { get; set; } = "";
}

public class ValidationResult
{
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; } = "";
}

public class ComparisonSummary
{
    public int TotalRecords { get; set; }
    public int ModifiedCount { get; set; }
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int UnchangedCount { get; set; }
}
