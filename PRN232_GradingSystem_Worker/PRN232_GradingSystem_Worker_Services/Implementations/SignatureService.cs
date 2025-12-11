using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PRN232_GradingSystem_Worker_Repo.Models;

namespace PRN232_GradingSystem_Worker_Services.Implementations;

/// <summary>
/// Service for calculating SimHash and MinHash signatures
/// </summary>
public sealed class SignatureService
{
    private const int SimHashBits = 64;

    /// <summary>
    /// Calculates 64-bit SimHash for code
    /// </summary>
    public long CalculateSimHash64(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return 0;

        var words = TokenizeCode(code);
        if (words.Count == 0)
            return 0;

        var hash = new int[SimHashBits];
        
        // Calculate weighted hash for each word
        foreach (var word in words)
        {
            var wordHash = ComputeHash(word);
            var weight = GetWordWeight(word);

            for (int i = 0; i < SimHashBits; i++)
            {
                if ((wordHash & (1L << i)) != 0)
                {
                    hash[i] += weight;
                }
                else
                {
                    hash[i] -= weight;
                }
            }
        }

        // Generate final hash bit string
        long simHash = 0;
        for (int i = 0; i < SimHashBits; i++)
        {
            if (hash[i] > 0)
            {
                simHash |= (1L << i);
            }
        }

        return simHash;
    }

    /// <summary>
    /// Calculates MinHash signature (128 hash values)
    /// </summary>
    public byte[] CalculateMinhash(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new byte[128];

        var shingles = GenerateShingles(code);
        if (shingles.Count == 0)
            return new byte[128];

        // Generate 128 hash values (one byte each)
        var minhash = new byte[128];
        var random = new Random(42); // Fixed seed for consistent results

        for (int i = 0; i < 128; i++)
        {
            byte minValue = byte.MaxValue;
            
            // Find minimum hash value across all shingles
            foreach (var shingle in shingles)
            {
                var hashValue = HashShingle(shingle, i, random);
                if (hashValue < minValue)
                {
                    minValue = hashValue;
                }
            }

            minhash[i] = minValue;
        }

        return minhash;
    }

    /// <summary>
    /// Calculates Hamming distance between two SimHash values
    /// </summary>
    public int HammingDistance(long hash1, long hash2)
    {
        var xor = hash1 ^ hash2;
        var distance = 0;

        while (xor != 0)
        {
            if ((xor & 1) != 0)
                distance++;
            xor >>= 1;
        }

        return distance;
    }

    /// <summary>
    /// Calculates similarity from Hamming distance (0-1 scale)
    /// </summary>
    public decimal CalculateSimHashSimilarity(long hash1, long hash2)
    {
        var distance = HammingDistance(hash1, hash2);
        var similarity = 1.0m - ((decimal)distance / SimHashBits);
        return Math.Max(0, similarity);
    }

    /// <summary>
    /// Tokenizes code into words
    /// </summary>
    private List<string> TokenizeCode(string code)
    {
        // Simple tokenization: split on whitespace and common delimiters
        var tokens = new List<string>();
        var builder = new StringBuilder();

        foreach (var c in code)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Generates shingles (overlapping sequences of words)
    /// </summary>
    private List<string> GenerateShingles(string code, int shingleSize = 3)
    {
        var words = TokenizeCode(code);
        var shingles = new List<string>();

        for (int i = 0; i <= words.Count - shingleSize; i++)
        {
            var shingle = string.Join(" ", words.Skip(i).Take(shingleSize));
            shingles.Add(shingle);
        }

        return shingles;
    }

    /// <summary>
    /// Computes simple hash for a word
    /// </summary>
    private long ComputeHash(string word)
    {
        var bytes = Encoding.UTF8.GetBytes(word);
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(bytes);
        return BitConverter.ToInt64(hashBytes, 0);
    }

    /// <summary>
    /// Gets weight for a word (common words get lower weight)
    /// </summary>
    private int GetWordWeight(string word)
    {
        var commonWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for",
            "of", "with", "by", "this", "that", "these", "those", "is", "are",
            "void", "return", "public", "private", "protected", "static", "class",
            "if", "else", "for", "while", "do", "try", "catch", "finally"
        };

        return commonWords.Contains(word) ? 1 : 2;
    }

    /// <summary>
    /// Hashes a shingle with a given index and seed
    /// </summary>
    private byte HashShingle(string shingle, int index, Random random)
    {
        var randomValue = random.Next(256);
        var shingleHash = shingle.GetHashCode() ^ (index * 31) ^ randomValue;
        return (byte)(Math.Abs(shingleHash) % 256);
    }
}

