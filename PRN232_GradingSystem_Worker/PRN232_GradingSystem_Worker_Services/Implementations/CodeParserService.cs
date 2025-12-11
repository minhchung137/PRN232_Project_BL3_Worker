using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PRN232_GradingSystem_Worker_Repo.Models;

namespace PRN232_GradingSystem_Worker_Services.Implementations;

/// <summary>
/// Service for parsing code and extracting code units (functions, classes, etc.)
/// </summary>
public sealed class CodeParserService
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".java", ".js", ".ts", ".cpp", ".c", ".h", ".hpp"
    };

    /// <summary>
    /// Determines programming language from file extension
    /// </summary>
    public string DetectLanguage(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return SupportedLanguages.Contains(extension) ? extension.ToLower() : ".txt";
    }

    /// <summary>
    /// Extracts code units (functions, classes, etc.) from source code
    /// </summary>
    public List<CodeUnitInfo> ExtractCodeUnits(string code, string filePath, string language)
    {
        var units = new List<CodeUnitInfo>();

        switch (language.ToLower())
        {
            case ".cs":
                ExtractCSharpUnits(code, filePath, units);
                break;
            case ".java":
                ExtractJavaUnits(code, filePath, units);
                break;
            case ".js":
            case ".ts":
                ExtractJavaScriptUnits(code, filePath, units);
                break;
            default:
                // For other languages, treat entire file as one unit
                units.Add(new CodeUnitInfo
                {
                    Kind = "File",
                    Key = Path.GetFileName(filePath),
                    Content = code,
                    StartLine = 1,
                    EndLine = code.Split('\n').Length
                });
                break;
        }

        return units;
    }

    private void ExtractCSharpUnits(string code, string filePath, List<CodeUnitInfo> units)
    {
        // Extract classes
        var classPattern = new Regex(@"\b(public|private|protected|internal)?\s*class\s+(\w+).*?\{", RegexOptions.Multiline | RegexOptions.Singleline);
        ExtractPattern(code, classPattern, "Class", filePath, units);

        // Extract methods
        var methodPattern = new Regex(@"\b(public|private|protected|internal)?\s*(\w+\s+)*(\w+)\s*\([^)]*\)\s*\{", RegexOptions.Multiline | RegexOptions.Singleline);
        ExtractPattern(code, methodPattern, "Method", filePath, units);
    }

    private void ExtractJavaUnits(string code, string filePath, List<CodeUnitInfo> units)
    {
        // Extract classes
        var classPattern = new Regex(@"\b(public|private|protected)?\s*class\s+(\w+).*?\{", RegexOptions.Multiline | RegexOptions.Singleline);
        ExtractPattern(code, classPattern, "Class", filePath, units);

        // Extract methods
        var methodPattern = new Regex(@"\b(public|private|protected)?\s*(\w+\s+)*(\w+)\s*\([^)]*\)\s*\{", RegexOptions.Multiline | RegexOptions.Singleline);
        ExtractPattern(code, methodPattern, "Method", filePath, units);
    }

    private void ExtractJavaScriptUnits(string code, string filePath, List<CodeUnitInfo> units)
    {
        // Extract function declarations
        var funcPattern = new Regex(@"\bfunction\s+(\w+)\s*\([^)]*\)\s*\{", RegexOptions.Multiline | RegexOptions.Singleline);
        ExtractPattern(code, funcPattern, "Function", filePath, units);

        // Extract arrow functions
        var arrowPattern = new Regex(@"\bconst\s+(\w+)\s*=\s*\([^)]*\)\s*=>\s*\{", RegexOptions.Multiline | RegexOptions.Singleline);
        ExtractPattern(code, arrowPattern, "ArrowFunction", filePath, units);

        // Extract classes
        var classPattern = new Regex(@"\bclass\s+(\w+).*?\{", RegexOptions.Multiline | RegexOptions.Singleline);
        ExtractPattern(code, classPattern, "Class", filePath, units);
    }

    private void ExtractPattern(string code, Regex pattern, string unitKind, string filePath, List<CodeUnitInfo> units)
    {
        var lines = code.Split('\n');
        var matches = pattern.Matches(code);

        foreach (Match match in matches)
        {
            var lineNumber = code.Substring(0, match.Index).Split('\n').Length;
            var unitName = match.Groups.Count > 1 ? match.Groups[2].Value : match.Groups[1].Value;
            
            // Find matching closing brace
            var braceDepth = 1;
            var endIndex = match.Index + match.Length;
            var endLineNumber = lineNumber;

            for (int i = endIndex; i < code.Length && braceDepth > 0; i++)
            {
                if (code[i] == '{')
                    braceDepth++;
                else if (code[i] == '}')
                    braceDepth--;

                if (code[i] == '\n')
                    endLineNumber++;
            }

            var unitContent = code.Substring(match.Index, endIndex - match.Index);
            
            units.Add(new CodeUnitInfo
            {
                Kind = unitKind,
                Key = $"{Path.GetFileName(filePath)}::{unitName}",
                Content = unitContent,
                StartLine = lineNumber,
                EndLine = endLineNumber
            });
        }
    }
}

/// <summary>
/// Information about a code unit
/// </summary>
public sealed class CodeUnitInfo
{
    public string Kind { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}

