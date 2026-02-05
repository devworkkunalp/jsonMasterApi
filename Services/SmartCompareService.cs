using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace JsonMaster.Api.Services;

public class SmartCompareService
{
    public ComparisonResult CompareJsons(string sourceJson, string targetJson, string keyField, string ignoredFields = "")
    {
        var result = new ComparisonResult();
        var ignoredSet = new HashSet<string>(
            ignoredFields.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
            StringComparer.OrdinalIgnoreCase
        );

        try
        {
            var source = JToken.Parse(sourceJson);
            var target = JToken.Parse(targetJson);


            if (ignoredSet.Any())
            {
                PruneIgnoredFields(source, ignoredSet);
                PruneIgnoredFields(target, ignoredSet);
            }


            var validation = ValidateKeyField(source, target, keyField);
            if (!validation.IsValid)
            {
                result.ValidationError = validation.ErrorMessage;
                return result;
            }


            if (source is JArray sourceArray && target is JArray targetArray)
            {
                CompareArrays(sourceArray, targetArray, keyField, ignoredSet, result);
            }
            // Handle object comparison
            else if (source is JObject sourceObj && target is JObject targetObj)
            {

                if (!string.IsNullOrWhiteSpace(keyField))
                {
                    // Look for array properties in both objects
                    var sourceArrayProp = sourceObj.Properties()
                        .FirstOrDefault(p => p.Value is JArray);
                    var targetArrayProp = targetObj.Properties()
                        .FirstOrDefault(p => p.Value is JArray);

                    if (sourceArrayProp != null && targetArrayProp != null && 
                        sourceArrayProp.Name == targetArrayProp.Name)
                    {
                        // Compare the arrays within the objects
                        CompareArrays(
                            sourceArrayProp.Value as JArray, 
                            targetArrayProp.Value as JArray, 
                            keyField,
                            ignoredSet,
                            result
                        );
                    }
                    else
                    {
                        result.ValidationError = $"Could not find matching array properties in both objects to compare with keyField '{keyField}'.";
                    }
                }
                else
                {
                    // No keyField provided, compare objects directly
                    var diff = CompareObjects(sourceObj, targetObj, "", ignoredSet);
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



    private ValidationResult ValidateKeyField(JToken source, JToken target, string keyField)
    {
        // If comparing objects directly and no key field provided, that's fine
        if (source is JObject sourceObj && target is JObject targetObj)
        {

            if (!string.IsNullOrWhiteSpace(keyField))
            {
                var sourceArrayProp = sourceObj.Properties().FirstOrDefault(p => p.Value is JArray);
                var targetArrayProp = targetObj.Properties().FirstOrDefault(p => p.Value is JArray);

                if (sourceArrayProp == null || targetArrayProp == null)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "Key field provided but no arrays found in the root objects to compare."
                    };
                }

                if (sourceArrayProp.Name != targetArrayProp.Name)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Array property names don't match: '{sourceArrayProp.Name}' vs '{targetArrayProp.Name}'."
                    };
                }

                // Validate keyField exists in the arrays
                var sourceArray = sourceArrayProp.Value as JArray;
                var targetArray = targetArrayProp.Value as JArray;

                if (sourceArray.Any() && sourceArray[0][keyField] == null)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Key field '{keyField}' not found in source array '{sourceArrayProp.Name}'."
                    };
                }

                if (targetArray.Any() && targetArray[0][keyField] == null)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Key field '{keyField}' not found in target array '{targetArrayProp.Name}'."
                    };
                }
            }

            return new ValidationResult { IsValid = true };
        }

        // For arrays, validate key field exists
        if (source is JArray srcArray && target is JArray tgtArray)
        {
            if (string.IsNullOrWhiteSpace(keyField))
            {
                return new ValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Key field is required for comparing arrays. Please specify a field to match records (e.g., 'id', 'workOrderID')."
                };
            }

            // Check if key field exists in source
            var sourceHasKey = srcArray.Any() && srcArray[0][keyField] != null;
            if (!sourceHasKey)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Key field '{keyField}' not found in source JSON. Please check the field name."
                };
            }

            // Check if key field exists in target
            var targetHasKey = tgtArray.Any() && tgtArray[0][keyField] != null;
            if (!targetHasKey)
            {
                return new ValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Key field '{keyField}' not found in target JSON. Please check the field name."
                };
            }

            return new ValidationResult { IsValid = true };
        }

        return new ValidationResult
        {
            IsValid = false,
            ErrorMessage = "Invalid JSON structure. Both files must contain arrays or objects."
        };
    }

    private void CompareArrays(JArray source, JArray target, string keyField, HashSet<string> ignoredSet, ComparisonResult result)
    {

        var sourceGroups = source.OfType<JObject>()
            .GroupBy(obj => obj[keyField]?.ToString() ?? "")
            .ToDictionary(g => g.Key, g => g.ToList());

        var targetGroups = target.OfType<JObject>()
            .GroupBy(obj => obj[keyField]?.ToString() ?? "")
            .ToDictionary(g => g.Key, g => g.ToList());

        var allKeys = sourceGroups.Keys.Union(targetGroups.Keys).Distinct();

        foreach (var key in allKeys)
        {
            var sourceItems = sourceGroups.ContainsKey(key) ? sourceGroups[key] : new List<JObject>();
            var targetItems = targetGroups.ContainsKey(key) ? targetGroups[key] : new List<JObject>();

            // Match items 1-to-1 within the group
            int count = Math.Max(sourceItems.Count, targetItems.Count);

            for (int i = 0; i < count; i++)
            {
                var src = i < sourceItems.Count ? sourceItems[i] : null;
                var tgt = i < targetItems.Count ? targetItems[i] : null;

                if (src != null && tgt != null)
                {
                    var diff = CompareObjects(src, tgt, "", ignoredSet);
                    diff.KeyValue = key;
                    if (diff.Differences.Any())
                    {
                        result.Modified.Add(diff);
                    }
                    else
                    {
                        result.Unchanged.Add(src);
                    }
                }
                else if (src != null)
                {
                    // Existed in Source but not matching index in Target (Removed)
                    result.Removed.Add(src);
                }
                else if (tgt != null)
                {
                    // Existed in Target but not matching index in Source (Added)
                    result.Added.Add(tgt);
                }
            }
        }
    }

    private ObjectDiff CompareObjects(JObject source, JObject target, string basePath, HashSet<string>? ignoredSet = null)
    {
        var diff = new ObjectDiff
        {
            Source = source,
            Target = target,
            Differences = new List<FieldDiff>()
        };

        var allKeys = source.Properties().Select(p => p.Name)
            .Union(target.Properties().Select(p => p.Name))
            .Distinct();
            
        // Filter out ignored keys
        if (ignoredSet != null && ignoredSet.Any())
        {
            allKeys = allKeys.Where(k => !ignoredSet.Contains(k));
        }

        foreach (var key in allKeys)
        {
            var path = string.IsNullOrEmpty(basePath) ? key : $"{basePath}.{key}";
            var sourceValue = source[key];
            var targetValue = target[key];

            // Field exists in source but not target
            if (sourceValue != null && targetValue == null)
            {
                diff.Differences.Add(new FieldDiff
                {
                    Path = path,
                    SourceValue = sourceValue,
                    TargetValue = null,
                    ChangeType = "removed"
                });
            }
            // Field exists in target but not source
            else if (sourceValue == null && targetValue != null)
            {
                diff.Differences.Add(new FieldDiff
                {
                    Path = path,
                    SourceValue = null,
                    TargetValue = targetValue,
                    ChangeType = "added"
                });
            }
            // Field exists in both
            else if (sourceValue != null && targetValue != null)
            {
                // Deep compare nested objects
                if (sourceValue is JObject sourceObj && targetValue is JObject targetObj)
                {
                    var nestedDiff = CompareObjects(sourceObj, targetObj, path, ignoredSet);
                    diff.Differences.AddRange(nestedDiff.Differences);
                }
                // Compare arrays
                else if (sourceValue is JArray sourceArr && targetValue is JArray targetArr)
                {
                    if (!JToken.DeepEquals(sourceArr, targetArr))
                    {
                        diff.Differences.Add(new FieldDiff
                        {
                            Path = path,
                            SourceValue = sourceArr,
                            TargetValue = targetArr,
                            ChangeType = "modified"
                        });
                    }
                }
                // Compare primitive values
                else if (!JToken.DeepEquals(sourceValue, targetValue))
                {
                    diff.Differences.Add(new FieldDiff
                    {
                        Path = path,
                        SourceValue = sourceValue,
                        TargetValue = targetValue,
                        ChangeType = "modified"
                    });
                }
            }
        }

        return diff;
    }

    private void PruneIgnoredFields(JToken token, HashSet<string> ignoredSet)
    {
        if (token is JObject obj)
        {
            // Find properties to remove
            var propsToRemove = obj.Properties()
                .Where(p => ignoredSet.Contains(p.Name))
                .ToList();

            foreach (var prop in propsToRemove)
            {
                prop.Remove();
            }

            // Recurse into remaining properties
            foreach (var prop in obj.Properties())
            {
                PruneIgnoredFields(prop.Value, ignoredSet);
            }
        }
        else if (token is JArray arr)
        {
            foreach (var item in arr)
            {
                PruneIgnoredFields(item, ignoredSet);
            }
        }
    }
}

// Result models
public class ComparisonResult
{
    public List<ObjectDiff> Modified { get; set; } = new();
    public List<JObject> Added { get; set; } = new();
    public List<JObject> Removed { get; set; } = new();
    public List<JObject> Unchanged { get; set; } = new();
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
}

public class ObjectDiff
{
    public string KeyValue { get; set; } = "";
    public JObject? Source { get; set; }
    public JObject? Target { get; set; }
    public List<FieldDiff> Differences { get; set; } = new();
}

public class FieldDiff
{
    public string Path { get; set; } = "";
    public object? SourceValue { get; set; }
    public object? TargetValue { get; set; }
    public string ChangeType { get; set; } = ""; // "added", "removed", "modified"
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
