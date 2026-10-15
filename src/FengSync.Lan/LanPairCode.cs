using System.Security.Cryptography;

namespace FengSync.Lan;

/// <summary>
/// The human-typed half of pairing. The alphabet drops the characters nobody can read off a screen at a
/// distance, because a code that must be typed into a phone by hand is a code that will be mis-typed.
/// </summary>
public static class LanPairCode
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 6;

    /// <summary>A fresh code from the cryptographic generator — not <see cref="Random"/>, which a peer could predict.</summary>
    public static string Generate()
    {
        Span<char> code = stackalloc char[Length];
        for (var index = 0; index < Length; index++)
            code[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(code);
    }

    /// <summary>
    /// Upper-cases and strips the separators a person types between code groups, then rejects anything that
    /// is not exactly one code. Done before any network call, so a stray character never reaches the server.
    /// </summary>
    public static bool TryNormalise(string? input, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        Span<char> buffer = stackalloc char[input.Length];
        var count = 0;
        foreach (var character in input.AsSpan())
        {
            var upper = char.ToUpperInvariant(character);
            if (upper is ' ' or '-' or '_' or '\t' or '.') continue;
            if (Alphabet.IndexOf(upper) < 0) return false;
            if (count == buffer.Length) return false;
            buffer[count++] = upper;
        }

        if (count != Length) return false;
        code = new string(buffer[..count]);
        return true;
    }
}
