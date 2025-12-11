using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PRN232_GradingSystem_Worker_Repo.Models;

namespace PRN232_GradingSystem_Worker_Services.Implementations;

/// <summary>
/// Service for calculating fingerprints using k-gram + Winnowing algorithm
/// </summary>
public sealed class FingerprintService
{
    private const int KGramSize = 5; // Length of k-grams
    private const int WindowSize = 5; // Winnowing window size

    /// <summary>
    /// Generates k-grams from source code
    /// </summary>
    public List<string> GenerateKGrams(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new List<string>();

        var normalized = NormalizeCode(code);
        var kgrams = new List<string>();

        for (int i = 0; i <= normalized.Length - KGramSize; i++)
        {
            var kgram = normalized.Substring(i, KGramSize);
            kgrams.Add(kgram);
        }

        return kgrams;
    }

    /// <summary>
    /// Applies Winnowing algorithm to select representative fingerprints
    /// </summary>
    public List<long> Winnowing(string code)
    {
        var kgrams = GenerateKGrams(code);
        if (kgrams.Count == 0)
            return new List<long>();

        var fingerprints = new List<long>();
        var hashValues = kgrams.Select(k => HashKGram(k)).ToList();

        // If we have fewer k-grams than window size, hash all of them
        if (hashValues.Count <= WindowSize)
        {
            return hashValues.Select((hash, index) => hash).ToList();
        }

        // Apply Winnowing: slide window and select minimum hash in each window
        for (int i = 0; i <= hashValues.Count - WindowSize; i++)
        {
            var window = hashValues.Skip(i).Take(WindowSize).ToList();
            var minHash = window.Min();
            var minIndex = window.IndexOf(minHash);

            // Add the minimum hash from the window
            fingerprints.Add(minHash);
        }

        // Deduplicate consecutive identical fingerprints
        var result = new List<long> { fingerprints[0] };
        for (int i = 1; i < fingerprints.Count; i++)
        {
            if (fingerprints[i] != fingerprints[i - 1])
            {
                result.Add(fingerprints[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Hashes a k-gram using MD5 and returns first 8 bytes as long
    /// </summary>
    private long HashKGram(string kgram)
    {
        using var md5 = MD5.Create();
        var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(kgram));
        
        // Take first 8 bytes and convert to long
        return BitConverter.ToInt64(hashBytes, 0);
    }

    /// <summary>
    /// Normalizes code by removing whitespace and converting to lowercase
    /// </summary>
    private string NormalizeCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;

        // Remove comments (simple approach)
        var lines = code.Split('\n');
        var normalizedLines = lines
            .Select(line =>
            {
                // Remove single-line comments
                var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIndex >= 0)
                {
                    line = line.Substring(0, commentIndex);
                }

                return line.Trim();
            })
            .Where(line => !string.IsNullOrWhiteSpace(line));

        var normalized = string.Join(" ", normalizedLines);
        
        // Convert to lowercase and remove extra whitespace
        normalized = normalized.ToLowerInvariant();
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");
        
        return normalized;
    }

    /// <summary>
    /// Calculates Jaccard similarity between two sets of fingerprints
    /// </summary>
    public decimal CalculateSimilarity(List<long> fingerprints1, List<long> fingerprints2)
    {
        if (fingerprints1.Count == 0 && fingerprints2.Count == 0)
            return 1.0m;

        if (fingerprints1.Count == 0 || fingerprints2.Count == 0)
            return 0.0m;

        var set1 = new HashSet<long>(fingerprints1);
        var set2 = new HashSet<long>(fingerprints2);

        var intersection = set1.Intersect(set2).Count();
        var union = set1.Union(set2).Count();

        return union > 0 ? (decimal)intersection / union : 0.0m;
    }
}

