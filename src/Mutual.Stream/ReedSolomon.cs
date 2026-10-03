namespace Mutual.Stream;

// error correction for video over udp. k data pieces plus m parity pieces and any k of them get the rest back
// reed-solomon over gf(256) with a cauchy matrix so any pick of rows can be inverted. k + m tops out at 256
public static class ReedSolomon
{
    static readonly byte[] Exp = new byte[512], Log = new byte[256];
    static readonly byte[] Mul = new byte[256 * 256];

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x; Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        for (int a = 1; a < 256; a++)
            for (int b = 1; b < 256; b++)
                Mul[a << 8 | b] = Exp[Log[a] + Log[b]];
    }

    static byte Inv(byte a) => a == 0 ? throw new DivideByZeroException() : Exp[255 - Log[a]];

    // cauchy coefficient for parity row i, data column j: 1 / (x_i + y_j), x_i = k + i, y_j = j
    static byte Coef(int k, int i, int j) => Inv((byte)((k + i) ^ j));

    // fills in the parity from the data, all the same length
    public static void Encode(byte[][] data, byte[][] parity, int len)
    {
        int k = data.Length;
        for (int i = 0; i < parity.Length; i++)
        {
            var p = parity[i];
            Array.Clear(p, 0, len);
            for (int j = 0; j < k; j++) MulAdd(p, data[j], Coef(k, i, j), len);
        }
    }

    // fixes missing data pieces in place (null = lost). false if fewer than k made it
    public static bool Reconstruct(byte[]?[] shards, int k, int len)
    {
        int n = shards.Length;
        var missing = new List<int>();
        for (int j = 0; j < k; j++) if (shards[j] == null) missing.Add(j);
        if (missing.Count == 0) return true;
        // pick k rows, every data row we have then parity rows to fill up
        var rows = new List<int>();
        for (int j = 0; j < k; j++) if (shards[j] != null) rows.Add(j);
        for (int i = k; i < n && rows.Count < k; i++) if (shards[i] != null) rows.Add(i);
        if (rows.Count < k) return false;

        // matrix of those rows (identity for data, cauchy for parity), then invert it
        var a = new byte[k, k];
        for (int r = 0; r < k; r++)
        {
            int src = rows[r];
            if (src < k) a[r, src] = 1;
            else for (int c = 0; c < k; c++) a[r, c] = Coef(k, src - k, c);
        }
        var inv = Invert(a, k);

        foreach (int d in missing)
        {
            var outp = new byte[len];
            for (int r = 0; r < k; r++)
            {
                byte c = inv[d, r];
                if (c != 0) MulAdd(outp, shards[rows[r]]!, c, len);
            }
            shards[d] = outp;
        }
        return true;
    }

    static void MulAdd(byte[] dst, byte[] src, byte c, int len)
    {
        if (c == 0) return;
        if (c == 1) { for (int b = 0; b < len; b++) dst[b] ^= src[b]; return; }
        int row = c << 8;
        for (int b = 0; b < len; b++) dst[b] ^= Mul[row | src[b]];
    }

    static byte[,] Invert(byte[,] m, int k)
    {
        var a = (byte[,])m.Clone();
        var inv = new byte[k, k];
        for (int i = 0; i < k; i++) inv[i, i] = 1;
        for (int col = 0; col < k; col++)
        {
            int piv = col;
            while (piv < k && a[piv, col] == 0) piv++;
            if (piv == k) throw new InvalidOperationException("Singular matrix.");
            if (piv != col)
                for (int c = 0; c < k; c++)
                {
                    (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
                    (inv[col, c], inv[piv, c]) = (inv[piv, c], inv[col, c]);
                }
            byte s = Inv(a[col, col]);
            for (int c = 0; c < k; c++) { a[col, c] = Mul[s << 8 | a[col, c]]; inv[col, c] = Mul[s << 8 | inv[col, c]]; }
            for (int r = 0; r < k; r++)
            {
                if (r == col || a[r, col] == 0) continue;
                byte f = a[r, col];
                for (int c = 0; c < k; c++) { a[r, c] ^= Mul[f << 8 | a[col, c]]; inv[r, c] ^= Mul[f << 8 | inv[col, c]]; }
            }
        }
        return inv;
    }
}
