using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Bytex.Adapters.Hyperliquid;

/// <summary>
/// Keccak-256, which is what this venue hashes with and is NOT the SHA3-256 the framework ships.
/// <para>
/// The two differ in one byte: the padding. SHA3-256 appends the domain-separation bits 0x06 that NIST added when it
/// standardised Keccak; the original Keccak that the whole EVM was built on appends 0x01. Everything else - the same
/// permutation, the same rate, the same capacity - is identical, so <see cref="SHA3_256"/> runs, returns 32 bytes
/// that look exactly like a hash, and produces a digest this venue will never agree with. There is no error anywhere:
/// the signature recovers some other address and the order is refused for belonging to an account nobody has heard of.
/// </para>
/// <para>
/// So it is written out here rather than borrowed. The vectors in the tests are the published Keccak-256 digests of
/// the empty string and of "abc", which is what makes this a measurement rather than a hope.
/// </para>
/// </summary>
internal static class Keccak256
{
    /// <summary>Bytes absorbed per permutation: 1600 bits of state less twice the 256-bit digest.</summary>
    private const int Rate = (1600 - (2 * 256)) / 8;

    /// <summary>Digest length in bytes.</summary>
    public const int Size = 32;

    /// <summary>Rounds of the Keccak-f[1600] permutation.</summary>
    private const int Rounds = 24;

    /// <summary>The 5x5 lane grid of the state, as a flat count.</summary>
    private const int Lanes = 25;

    /// <summary>The original Keccak pad byte. SHA3 uses 0x06 here, which is the whole of the difference.</summary>
    private const byte PadFirst = 0x01;

    /// <summary>The bit that closes the padded block, on the last byte of the rate.</summary>
    private const byte PadLast = 0x80;

    private static readonly ulong[] _roundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL, 0x8000000080008000UL,
        0x000000000000808bUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008aUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800aUL, 0x800000008000000aUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL,
    ];

    private static readonly int[] _rotations =
    [
        0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43, 25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14,
    ];

    /// <summary>The Keccak-256 digest of the given bytes.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        ulong[] state = new ulong[Lanes];
        int offset = 0;
        while (data.Length - offset >= Rate)
        {
            Absorb(state, data.Slice(offset, Rate));
            Permute(state);
            offset += Rate;
        }

        // The final block, padded. A message that happens to end exactly on the rate still gets a whole padded block
        // of its own, which is what the two pad bytes landing on the same byte covers when the remainder is Rate - 1.
        Span<byte> tail = stackalloc byte[Rate];
        tail.Clear();
        data[offset..].CopyTo(tail);
        tail[data.Length - offset] = PadFirst;
        tail[Rate - 1] |= PadLast;
        Absorb(state, tail);
        Permute(state);

        byte[] digest = new byte[Size];
        for (int i = 0; i < Size / sizeof(ulong); i++)
        {
            BitConverter.TryWriteBytes(digest.AsSpan(i * sizeof(ulong)), state[i]);
        }

        return digest;
    }

    /// <summary>The digest of a UTF-8 string, which is how every EIP-712 type and value string is hashed.</summary>
    public static byte[] Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    private static void Absorb(ulong[] state, ReadOnlySpan<byte> block)
    {
        for (int i = 0; i < Rate / sizeof(ulong); i++)
        {
            state[i] ^= BitConverter.ToUInt64(block.Slice(i * sizeof(ulong), sizeof(ulong)));
        }
    }

    private static void Permute(ulong[] a)
    {
        Span<ulong> c = stackalloc ulong[5];
        Span<ulong> b = stackalloc ulong[Lanes];
        for (int round = 0; round < Rounds; round++)
        {
            // Theta: fold each column's parity into its neighbours.
            for (int x = 0; x < 5; x++)
            {
                c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            }

            for (int x = 0; x < 5; x++)
            {
                ulong d = c[(x + 4) % 5] ^ BitOperations.RotateLeft(c[(x + 1) % 5], 1);
                for (int y = 0; y < 5; y++)
                {
                    a[x + (5 * y)] ^= d;
                }
            }

            // Rho and pi: rotate each lane by its own amount and move it to its own place.
            for (int x = 0; x < 5; x++)
            {
                for (int y = 0; y < 5; y++)
                {
                    int from = x + (5 * y);
                    b[y + (5 * (((2 * x) + (3 * y)) % 5))] = BitOperations.RotateLeft(a[from], _rotations[from]);
                }
            }

            // Chi: the only non-linear step.
            for (int y = 0; y < 5; y++)
            {
                for (int x = 0; x < 5; x++)
                {
                    a[x + (5 * y)] = b[x + (5 * y)] ^ (~b[((x + 1) % 5) + (5 * y)] & b[((x + 2) % 5) + (5 * y)]);
                }
            }

            // Iota: break the symmetry between rounds.
            a[0] ^= _roundConstants[round];
        }
    }
}

/// <summary>
/// secp256k1 over <see cref="BigInteger"/>: the key derivation and the recoverable ECDSA signature this venue's
/// authentication is made of.
/// <para>
/// Written out because the framework cannot do it. <see cref="ECDsa"/> will sign over a custom curve, and it gives
/// back r and s and nothing else - and an Ethereum signature is three values. The recovery id that lets a verifier
/// work out WHICH key signed, without being told, is the whole point here: this venue is handed a signature and no
/// key, and it recovers the signer from the digest. Nothing in the framework exposes the point arithmetic that needs.
/// It also will not normalise s into the lower half of the order, which this venue requires and which the framework
/// has no notion of.
/// </para>
/// <para>
/// So the arithmetic is here, in Jacobian coordinates so that one modular inversion is done per signature rather than
/// one per bit of the scalar. The nonce is RFC 6979 deterministic - HMAC-SHA256, which the framework does have - so a
/// signature over one action is the same bytes every time and a test can pin it. A random nonce would make every
/// signature unrepeatable, and a nonce generator with a bias leaks the private key.
/// </para>
/// </summary>
internal static class Secp256k1
{
    // The curve's four constants, each written as eight groups of eight hex digits and read as 32 BYTES rather than
    // parsed as a hex number. Both of those are deliberate, and the second one is a bug that already happened here:
    // written as one 64-character literal, the prime was one F short, BigInteger.Parse accepted the 63-digit number
    // without complaint, and every point came out of the multiplication reduced modulo the wrong field. Nothing
    // threw. The addresses were 20 plausible bytes and the signatures were valid signatures over the wrong curve,
    // and the only thing that showed it was a published test vector disagreeing.
    //
    // Reading them as bytes makes the length part of the value: Convert.FromHexString refuses an odd digit count,
    // and Bytes() refuses anything that is not exactly 32. CurveIsSecp256k1Tests checks the generator lies on the
    // curve, which no typo survives.

    /// <summary>The field's prime: 2^256 - 2^32 - 977.</summary>
    private static readonly BigInteger _p = Bytes(
        "FFFFFFFF", "FFFFFFFF", "FFFFFFFF", "FFFFFFFF", "FFFFFFFF", "FFFFFFFF", "FFFFFFFE", "FFFFFC2F");

    /// <summary>The order of the generator: how many distinct scalars there are.</summary>
    private static readonly BigInteger _n = Bytes(
        "FFFFFFFF", "FFFFFFFF", "FFFFFFFF", "FFFFFFFE", "BAAEDCE6", "AF48A03B", "BFD25E8C", "D0364141");

    private static readonly BigInteger _gx = Bytes(
        "79BE667E", "F9DCBBAC", "55A06295", "CE870B07", "029BFCDB", "2DCE28D9", "59F2815B", "16F81798");

    private static readonly BigInteger _gy = Bytes(
        "483ADA77", "26A3C465", "5DA4FBFC", "0E1108A8", "FD17B448", "A6855419", "9C47D08F", "FB10D4B8");

    /// <summary>A scalar, a coordinate and half of a signature are all this many bytes.</summary>
    public const int ScalarSize = 32;

    /// <summary>How many groups of eight hex digits a 256-bit constant is written as.</summary>
    private const int ConstantGroups = 8;

    /// <summary>
    /// A curve constant from its hex groups, refusing anything that is not exactly 256 bits. The length check is the
    /// point: a missing digit in a 256-bit constant is invisible to a person reading it and fatal to the arithmetic.
    /// </summary>
    private static BigInteger Bytes(params string[] groups)
    {
        if (groups.Length != ConstantGroups)
        {
            throw new ArgumentException($"A secp256k1 constant is {ConstantGroups} groups of eight hex digits.", nameof(groups));
        }

        byte[] bytes = Convert.FromHexString(string.Concat(groups));
        return bytes.Length == ScalarSize
            ? new BigInteger(bytes, isUnsigned: true, isBigEndian: true)
            : throw new ArgumentException($"A secp256k1 constant is {ScalarSize} bytes, and this one is {bytes.Length}.", nameof(groups));
    }

    /// <summary>The length of an address in bytes, which is the last 20 of a hashed public key.</summary>
    public const int AddressSize = 20;

    /// <summary>
    /// What a recovery id is offset by in the <c>v</c> this venue is sent. The bare id is 0 or 1; Ethereum's
    /// signatures have carried it as 27 or 28 since the beginning and every verifier subtracts it back off.
    /// </summary>
    public const int RecoveryIdOffset = 27;

    /// <summary>One point of the curve, or the point at infinity when <see cref="IsInfinity"/>.</summary>
    private readonly record struct Point(BigInteger X, BigInteger Y, bool IsInfinity)
    {
        public static Point Infinity => new(BigInteger.Zero, BigInteger.Zero, true);
    }

    /// <summary>
    /// The 20-byte address a private key controls: the last 20 bytes of the Keccak-256 of its uncompressed public
    /// key with the 0x04 prefix dropped. This is the only way an adapter can know which account it is about to trade,
    /// because the venue is never told the key and never sends the address back.
    /// </summary>
    public static byte[] AddressOf(ReadOnlySpan<byte> privateKey)
    {
        byte[] publicKey = PublicKey(privateKey);
        byte[] hash = Keccak256.Hash(publicKey);
        return hash[(Keccak256.Size - AddressSize)..];
    }

    /// <summary>The uncompressed public key without its 0x04 prefix: x then y, 32 bytes each.</summary>
    public static byte[] PublicKey(ReadOnlySpan<byte> privateKey)
    {
        BigInteger d = Scalar(privateKey);
        Point q = Multiply(d, new Point(_gx, _gy, false));
        byte[] key = new byte[2 * ScalarSize];
        WriteScalar(q.X, key.AsSpan(0, ScalarSize));
        WriteScalar(q.Y, key.AsSpan(ScalarSize, ScalarSize));
        return key;
    }

    /// <summary>
    /// A recoverable signature over a 32-byte digest: r, s and the recovery id, already offset the way a verifier
    /// expects it.
    /// <para>
    /// Two normalisations happen here and both are required rather than cosmetic. s is folded into the lower half of
    /// the order, because (r, s) and (r, n - s) are both valid signatures over the same digest and a verifier that
    /// rejects the high one - as the EVM has since EIP-2 - refuses half the signatures at random. The recovery id is
    /// flipped to match, because folding s reflects the point the verifier will recover.
    /// </para>
    /// </summary>
    public static (byte[] R, byte[] S, int V) SignRecoverable(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> digest)
    {
        if (digest.Length != ScalarSize)
        {
            throw new ArgumentException($"A secp256k1 signature covers a {ScalarSize}-byte digest, not {digest.Length}.", nameof(digest));
        }

        BigInteger d = Scalar(privateKey);
        BigInteger z = new(digest, isUnsigned: true, isBigEndian: true);
        byte[] keyBytes = privateKey.ToArray();

        // RFC 6979 can hand back a k that produces r == 0 or s == 0, which is invalid and must be retried with the
        // next candidate rather than signed with. It has never been observed and the loop is what makes that a fact
        // about probability rather than an assumption about it.
        foreach (BigInteger k in Rfc6979.Nonces(keyBytes, digest.ToArray(), _n))
        {
            Point point = Multiply(k, new Point(_gx, _gy, false));
            BigInteger r = point.X % _n;
            if (r.IsZero)
            {
                continue;
            }

            BigInteger s = ModInverse(k, _n) * (z + (r * d)) % _n;
            if (s.IsZero)
            {
                continue;
            }

            // Whether the recovered point's x had wrapped past the order, and whether its y was odd: together they
            // are what tells a verifier which of the curve's points to rebuild.
            int recoveryId = (point.Y.IsEven ? 0 : 1) | (point.X >= _n ? 2 : 0);
            if (s > _n / 2)
            {
                s = _n - s;
                recoveryId ^= 1;
            }

            byte[] rBytes = new byte[ScalarSize];
            byte[] sBytes = new byte[ScalarSize];
            WriteScalar(r, rBytes);
            WriteScalar(s, sBytes);
            return (rBytes, sBytes, recoveryId + RecoveryIdOffset);
        }

        throw new InvalidOperationException("RFC 6979 produced no usable nonce for this key and digest.");
    }

    /// <summary>A private key as a scalar, refused when it is not one the curve has.</summary>
    private static BigInteger Scalar(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != ScalarSize)
        {
            throw new ArgumentException($"A secp256k1 private key is {ScalarSize} bytes, not {privateKey.Length}.", nameof(privateKey));
        }

        BigInteger d = new(privateKey, isUnsigned: true, isBigEndian: true);
        if (d.IsZero || d >= _n)
        {
            throw new ArgumentException("This is not a secp256k1 private key: it is zero or past the curve's order.", nameof(privateKey));
        }

        return d;
    }

    private static void WriteScalar(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        bytes.CopyTo(destination[(destination.Length - bytes.Length)..]);
    }

    /// <summary>
    /// The inverse of <paramref name="value"/> modulo a PRIME <paramref name="modulus"/>, by Fermat's little theorem.
    /// Both moduli here are prime, which is what makes exponentiation a legitimate shortcut past an extended
    /// Euclidean algorithm and its sign handling.
    /// </summary>
    private static BigInteger ModInverse(BigInteger value, BigInteger modulus) =>
        BigInteger.ModPow(BigInteger.Remainder(value, modulus) + modulus, modulus - 2, modulus);

    /// <summary>
    /// The scalar multiple of a point, double-and-add in Jacobian coordinates. Jacobian rather than affine because
    /// affine needs a modular inversion for every bit of the scalar - 256 of them, each an exponentiation - where
    /// this needs exactly one, at the end.
    /// </summary>
    private static Point Multiply(BigInteger scalar, Point point)
    {
        (BigInteger x, BigInteger y, BigInteger z) result = (BigInteger.One, BigInteger.One, BigInteger.Zero);
        (BigInteger x, BigInteger y, BigInteger z) addend = (point.X, point.Y, BigInteger.One);

        // Constant-time is not attempted and is not needed: nothing here signs on behalf of anybody but the holder of
        // the key, in a process the holder started, so there is no observer to leak a bit to.
        for (int bit = 0; bit < scalar.GetBitLength(); bit++)
        {
            if (!((scalar >> bit) & BigInteger.One).IsZero)
            {
                result = JacobianAdd(result, addend);
            }

            addend = JacobianDouble(addend);
        }

        if (result.z.IsZero)
        {
            return Point.Infinity;
        }

        BigInteger inverse = ModInverse(result.z, _p);
        BigInteger inverseSquared = inverse * inverse % _p;
        return new Point(
            result.x * inverseSquared % _p,
            result.y * inverseSquared % _p * inverse % _p,
            false);
    }

    private static (BigInteger X, BigInteger Y, BigInteger Z) JacobianDouble((BigInteger X, BigInteger Y, BigInteger Z) p)
    {
        if (p.Y.IsZero || p.Z.IsZero)
        {
            return (BigInteger.One, BigInteger.One, BigInteger.Zero);
        }

        // The standard doubling for a curve whose a coefficient is zero, which secp256k1's is.
        BigInteger ysq = p.Y * p.Y % _p;
        BigInteger s = 4 * p.X * ysq % _p;
        BigInteger m = 3 * p.X * p.X % _p;
        BigInteger x = Mod((m * m) - (2 * s));
        BigInteger y = Mod((m * (s - x)) - (8 * ysq * ysq));
        BigInteger z = 2 * p.Y * p.Z % _p;
        return (x, y, z);
    }

    private static (BigInteger X, BigInteger Y, BigInteger Z) JacobianAdd((BigInteger X, BigInteger Y, BigInteger Z) a, (BigInteger X, BigInteger Y, BigInteger Z) b)
    {
        if (a.Z.IsZero)
        {
            return b;
        }

        if (b.Z.IsZero)
        {
            return a;
        }

        BigInteger az2 = a.Z * a.Z % _p;
        BigInteger bz2 = b.Z * b.Z % _p;
        BigInteger u1 = a.X * bz2 % _p;
        BigInteger u2 = b.X * az2 % _p;
        BigInteger s1 = a.Y * bz2 % _p * b.Z % _p;
        BigInteger s2 = b.Y * az2 % _p * a.Z % _p;
        if (u1 == u2)
        {
            // The same x: either the same point, which doubles, or a point and its negation, which is infinity.
            return s1 == s2 ? JacobianDouble(a) : (BigInteger.One, BigInteger.One, BigInteger.Zero);
        }

        BigInteger h = Mod(u2 - u1);
        BigInteger rr = Mod(s2 - s1);
        BigInteger h2 = h * h % _p;
        BigInteger h3 = h2 * h % _p;
        BigInteger u1h2 = u1 * h2 % _p;
        BigInteger x = Mod((rr * rr) - h3 - (2 * u1h2));
        BigInteger y = Mod((rr * (u1h2 - x)) - (s1 * h3));
        BigInteger z = h * a.Z % _p * b.Z % _p;
        return (x, y, z);
    }

    /// <summary>A value reduced into the field, taking care of the negative remainder BigInteger returns.</summary>
    private static BigInteger Mod(BigInteger value)
    {
        BigInteger remainder = BigInteger.Remainder(value, _p);
        return remainder.Sign < 0 ? remainder + _p : remainder;
    }
}

/// <summary>
/// The deterministic nonce of RFC 6979, which is what a signature is repeatable and testable because of. Built from
/// HMAC-SHA256 over the private key and the digest, so the same action signed twice is the same bytes twice.
/// </summary>
internal static class Rfc6979
{
    /// <summary>The candidates in order, so that a caller can skip one that turns out unusable.</summary>
    public static IEnumerable<BigInteger> Nonces(byte[] privateKey, byte[] digest, BigInteger order)
    {
        byte[] v = new byte[Secp256k1.ScalarSize];
        byte[] k = new byte[Secp256k1.ScalarSize];
        Array.Fill(v, (byte)0x01);

        byte[] seed = [.. privateKey, .. digest];
        k = HMACSHA256.HashData(k, Concat(v, 0x00, seed));
        v = HMACSHA256.HashData(k, v);
        k = HMACSHA256.HashData(k, Concat(v, 0x01, seed));
        v = HMACSHA256.HashData(k, v);

        while (true)
        {
            v = HMACSHA256.HashData(k, v);
            BigInteger candidate = new(v, isUnsigned: true, isBigEndian: true);
            if (!candidate.IsZero && candidate < order)
            {
                yield return candidate;
            }

            k = HMACSHA256.HashData(k, Concat(v, 0x00, []));
            v = HMACSHA256.HashData(k, v);
        }
    }

    /// <summary>
    /// The HMAC input RFC 6979 builds at each step: the running value, one separator byte, then the seed. Spelled out
    /// as a byte array because the two HMAC overloads are ambiguous for a collection expression, and a span here
    /// would have to be materialised anyway.
    /// </summary>
    private static byte[] Concat(byte[] value, byte separator, byte[] seed)
    {
        byte[] input = new byte[value.Length + 1 + seed.Length];
        value.CopyTo(input, 0);
        input[value.Length] = separator;
        seed.CopyTo(input, value.Length + 1);
        return input;
    }
}

/// <summary>
/// The MessagePack subset this venue's action hashing needs.
/// <para>
/// A whole serialisation format for one hash looks like overreach until you see why it cannot be avoided: the venue
/// does NOT hash the JSON it was sent. It parses the action into its own types and re-encodes it as MessagePack, and
/// THAT is what the signature covers. So an adapter has to produce the same bytes the venue will produce, which
/// means the same encoding and - because a MessagePack map is an ordered list of pairs - the same field ORDER. JSON
/// key order is irrelevant on the wire and decisive here.
/// </para>
/// <para>
/// Get it wrong and there is no error to see. The digest differs, the signature recovers a perfectly valid address
/// that belongs to nobody, and the venue refuses the order as coming from an account it has never heard of. Which is
/// exactly what makes it verifiable: sign an action with a key nobody has used, send it, and the venue names the
/// address it recovered. If that is the key's own address, every byte of this file matched what the venue built. The
/// tests do that against recorded answers, and it was done against the live venue while this was written.
/// </para>
/// <para>
/// Only the types an action is made of are here - maps with string keys, strings, unsigned integers, booleans and
/// arrays. No floats, because nothing in an action is one: a price and a size both travel as decimal STRINGS, which
/// is the venue's choice and a good one, since a float would put the rounding of a price inside the hash.
/// </para>
/// </summary>
internal static class ActionPack
{
    private const byte FixMapPrefix = 0x80;
    private const byte FixArrayPrefix = 0x90;
    private const byte FixStrPrefix = 0xa0;

    /// <summary>The largest count a fix-prefixed map, array or string can carry in its own first byte.</summary>
    private const int MaxFixCount = 15;

    private const byte Nil = 0xc0;
    private const byte False = 0xc2;
    private const byte True = 0xc3;
    private const byte Uint8 = 0xcc;
    private const byte Uint16 = 0xcd;
    private const byte Uint32 = 0xce;
    private const byte Uint64 = 0xcf;
    private const byte Str8 = 0xd9;
    private const byte Str16 = 0xda;
    private const byte Array16 = 0xdc;
    private const byte Map16 = 0xde;

    /// <summary>The largest value a positive fixint carries in its own single byte.</summary>
    private const int MaxFixInt = 0x7f;

    /// <summary>
    /// The MessagePack encoding of an action. An ordered list of pairs models a map on purpose: a dictionary would
    /// hide the field order, which is the one thing about this encoding that cannot be got wrong quietly.
    /// </summary>
    public static byte[] Encode(PackValue value)
    {
        using MemoryStream stream = new();
        Write(stream, value);
        return stream.ToArray();
    }

    private static void Write(Stream stream, PackValue value)
    {
        switch (value)
        {
            case PackMap map:
                WriteCount(stream, map.Fields.Count, FixMapPrefix, Map16);
                foreach ((string name, PackValue field) in map.Fields)
                {
                    WriteString(stream, name);
                    Write(stream, field);
                }

                break;

            case PackArray array:
                WriteCount(stream, array.Items.Count, FixArrayPrefix, Array16);
                foreach (PackValue item in array.Items)
                {
                    Write(stream, item);
                }

                break;

            case PackText text:
                WriteString(stream, text.Value);
                break;

            case PackNumber number:
                WriteUnsigned(stream, number.Value);
                break;

            case PackBool flag:
                stream.WriteByte(flag.Value ? True : False);
                break;

            default:
                stream.WriteByte(Nil);
                break;
        }
    }

    private static void WriteCount(Stream stream, int count, byte fixPrefix, byte wide16)
    {
        if (count <= MaxFixCount)
        {
            stream.WriteByte((byte)(fixPrefix | count));
            return;
        }

        stream.WriteByte(wide16);
        stream.WriteByte((byte)(count >> 8));
        stream.WriteByte((byte)count);
    }

    private static void WriteString(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= MaxFixCount)
        {
            stream.WriteByte((byte)(FixStrPrefix | bytes.Length));
        }
        else if (bytes.Length <= byte.MaxValue)
        {
            stream.WriteByte(Str8);
            stream.WriteByte((byte)bytes.Length);
        }
        else
        {
            stream.WriteByte(Str16);
            stream.WriteByte((byte)(bytes.Length >> 8));
            stream.WriteByte((byte)bytes.Length);
        }

        stream.Write(bytes);
    }

    /// <summary>
    /// An unsigned integer in the smallest form that holds it, which is what the venue's own encoder does and
    /// therefore what the hash is over. A number written wider than it needs to be is a different byte string and a
    /// different digest.
    /// </summary>
    private static void WriteUnsigned(Stream stream, ulong value)
    {
        if (value <= MaxFixInt)
        {
            stream.WriteByte((byte)value);
        }
        else if (value <= byte.MaxValue)
        {
            stream.WriteByte(Uint8);
            stream.WriteByte((byte)value);
        }
        else if (value <= ushort.MaxValue)
        {
            stream.WriteByte(Uint16);
            WriteBigEndian(stream, value, sizeof(ushort));
        }
        else if (value <= uint.MaxValue)
        {
            stream.WriteByte(Uint32);
            WriteBigEndian(stream, value, sizeof(uint));
        }
        else
        {
            stream.WriteByte(Uint64);
            WriteBigEndian(stream, value, sizeof(ulong));
        }
    }

    private static void WriteBigEndian(Stream stream, ulong value, int width)
    {
        for (int shift = (width - 1) * 8; shift >= 0; shift -= 8)
        {
            stream.WriteByte((byte)(value >> shift));
        }
    }
}

/// <summary>One value of an action, in the shapes MessagePack needs and no others.</summary>
internal abstract record PackValue;

/// <summary>
/// A map as an ORDERED list of fields. Order is part of the encoding and therefore part of the signature, so it
/// cannot be left to a dictionary's iteration.
/// </summary>
internal sealed record PackMap(IReadOnlyList<(string Name, PackValue Value)> Fields) : PackValue
{
    public static PackMap Of(params (string Name, PackValue Value)[] fields) => new(fields);
}

internal sealed record PackArray(IReadOnlyList<PackValue> Items) : PackValue
{
    public static PackArray Of(IEnumerable<PackValue> items) => new([.. items]);
}

internal sealed record PackText(string Value) : PackValue;

internal sealed record PackNumber(ulong Value) : PackValue;

internal sealed record PackBool(bool Value) : PackValue;

/// <summary>A signature as this venue takes it: r and s as hex, and the recovery id as a number.</summary>
public sealed record HyperliquidSignature(string R, string S, int V);

/// <summary>
/// What this venue calls authentication: an EIP-712 typed-data signature over the action, made with a secp256k1
/// private key.
/// <para>
/// This is the first venue here whose credential is not a key pair the exchange issued. There is no API key, no
/// secret and no passphrase, and nothing to revoke from a settings page - the credential is a private key that
/// controls an address, and the address IS the account. Which key it is decides what can be done with it: the
/// account's own wallet key can do everything including moving funds, while an API wallet the account has approved
/// can trade and cannot withdraw. The venue cannot tell an adapter which one it was given, so the adapter is told.
/// </para>
/// <para>
/// The digest is built in two stages, and the first is the one that is easy to get subtly wrong. The action is
/// MessagePack-encoded, the nonce is appended as eight big-endian bytes, and a vault byte follows - a bare zero when
/// there is no vault. Keccak-256 over that is the "connection id". Only then does EIP-712 begin, over a two-field
/// struct that carries the connection id and one letter saying which network this is: "a" for mainnet, "b" for
/// testnet. That letter is the whole of the replay protection between the two, which is why it is not a detail: a
/// mainnet signature is a valid testnet signature the moment it is wrong.
/// </para>
/// </summary>
public static class HyperliquidSigner
{
    /// <summary>The EIP-712 domain this venue signs in. Not a real chain id - the exchange is not a contract call.</summary>
    public const long ChainId = 1337;

    /// <summary>The domain name of the typed data, which is part of the domain separator and so part of every digest.</summary>
    public const string DomainName = "Exchange";

    /// <summary>The domain version, likewise.</summary>
    public const string DomainVersion = "1";

    /// <summary>The struct the connection id is wrapped in before it is signed.</summary>
    public const string PrimaryTypeSignature = "Agent(string source,bytes32 connectionId)";

    /// <summary>The domain's own type, hashed into the separator.</summary>
    public const string DomainTypeSignature = "EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)";

    /// <summary>The source string that says a signature is for mainnet.</summary>
    public const string MainnetSource = "a";

    /// <summary>And for testnet. One letter apart, and the only thing keeping one network's signatures off the other.</summary>
    public const string TestnetSource = "b";

    /// <summary>Bytes of an EIP-712 word, which every value in typed data is padded to.</summary>
    private const int WordSize = 32;

    /// <summary>The two bytes EIP-191 puts in front of typed data so it can never be a plain signed message.</summary>
    private static readonly byte[] _typedDataPrefix = [0x19, 0x01];

    /// <summary>
    /// The connection id: Keccak-256 over the packed action, the nonce and the vault byte. Public because it is the
    /// half that can be checked without a key at all - the same action and nonce must always give the same id, and a
    /// recorded one pins the field order this venue expects.
    /// </summary>
    public static byte[] ConnectionId(byte[] packedAction, long nonce, ReadOnlySpan<byte> vaultAddress)
    {
        ArgumentNullException.ThrowIfNull(packedAction);
        List<byte> data = [.. packedAction];
        for (int shift = 56; shift >= 0; shift -= 8)
        {
            data.Add((byte)(nonce >> shift));
        }

        // A single zero says "no vault". With one, the byte is a one and the address follows - so the presence of a
        // vault changes the length of what is hashed, and an adapter that always wrote the zero could never trade
        // one.
        if (vaultAddress.IsEmpty)
        {
            data.Add(0x00);
        }
        else
        {
            data.Add(0x01);
            data.AddRange(vaultAddress);
        }

        return Keccak256.Hash(data.ToArray());
    }

    /// <summary>
    /// The EIP-712 digest of an action, which is what is actually signed. Separate from
    /// <see cref="Sign"/> so a test can pin the digest of a recorded action without holding a key.
    /// </summary>
    public static byte[] Digest(byte[] packedAction, long nonce, ReadOnlySpan<byte> vaultAddress, bool mainnet)
    {
        byte[] connectionId = ConnectionId(packedAction, nonce, vaultAddress);

        // The domain separator: the domain's type hash, then its four values, each in a 32-byte word. A string value
        // is carried as its own hash rather than inline, which is what stops two different domains colliding by
        // having their fields run together.
        byte[] domain =
        [
            .. Keccak256.Hash(DomainTypeSignature),
            .. Keccak256.Hash(DomainName),
            .. Keccak256.Hash(DomainVersion),
            .. Word(ChainId),
            .. new byte[WordSize],
        ];

        byte[] structHash =
        [
            .. Keccak256.Hash(PrimaryTypeSignature),
            .. Keccak256.Hash(mainnet ? MainnetSource : TestnetSource),
            .. connectionId,
        ];

        return Keccak256.Hash([.. _typedDataPrefix, .. Keccak256.Hash(domain), .. Keccak256.Hash(structHash)]);
    }

    /// <summary>
    /// The Keccak-256 of some bytes, which is the hash this venue's whole authentication is built on.
    /// <para>
    /// Public because it is the one part of the pipeline that can be checked against something outside this
    /// repository. It is NOT the framework's SHA3-256, which differs from it by one pad byte and will happily return
    /// 32 bytes that look exactly like a digest and that this venue will never agree with, so a caller holding a
    /// published vector can settle the question here rather than by sending an order and reading a refusal.
    /// </para>
    /// </summary>
    public static byte[] Hash(ReadOnlySpan<byte> data) => Keccak256.Hash(data);

    /// <summary>The signature of an action, ready to be put beside it in the request body.</summary>
    public static HyperliquidSignature Sign(byte[] packedAction, long nonce, ReadOnlySpan<byte> vaultAddress, bool mainnet, ReadOnlySpan<byte> privateKey)
    {
        byte[] digest = Digest(packedAction, nonce, vaultAddress, mainnet);
        (byte[] r, byte[] s, int v) = Secp256k1.SignRecoverable(privateKey, digest);
        return new HyperliquidSignature(Hex.Encode(r), Hex.Encode(s), v);
    }

    /// <summary>A number in a 32-byte big-endian word, which is how EIP-712 carries every integer.</summary>
    private static byte[] Word(long value)
    {
        byte[] word = new byte[WordSize];
        for (int i = 0; i < sizeof(long); i++)
        {
            word[WordSize - 1 - i] = (byte)(value >> (8 * i));
        }

        return word;
    }
}

/// <summary>Hex with the 0x prefix this venue writes everywhere, in both directions.</summary>
internal static class Hex
{
    public const string Prefix = "0x";

    public static string Encode(ReadOnlySpan<byte> bytes) => Prefix + Convert.ToHexStringLower(bytes);

    /// <summary>
    /// The bytes of a hex string, with or without the prefix. A private key and an address are both read from
    /// configuration as text, and a person pastes them either way - so both are accepted rather than one being an
    /// error a person cannot see the cause of.
    /// </summary>
    public static byte[] Decode(string text, int expectedLength, string what)
    {
        ArgumentNullException.ThrowIfNull(text);
        string body = text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? text[Prefix.Length..] : text;
        if (body.Length != expectedLength * 2)
        {
            throw new ArgumentException(
                $"A {what} is {expectedLength} bytes, so {expectedLength * 2} hex characters with or without a 0x "
                + $"prefix. This one has {body.Length}.",
                nameof(text));
        }

        try
        {
            return Convert.FromHexString(body);
        }
        catch (FormatException e)
        {
            throw new ArgumentException($"A {what} has to be hex, and this one is not.", nameof(text), e);
        }
    }
}
