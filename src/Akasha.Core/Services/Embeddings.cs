namespace Akasha.Core.Services;

/// <summary>
/// Абстракция эмбеддингов (BGE/rubert/distiluse и т.п.). Размерность задаётся моделью.
/// Офлайн-модель ONNX подключается отдельно; здесь — детерминированный fallback-генератор
/// (hash-based bag-of-character-ngrams), который даёт осмысленный косинус для похожих текстов.
/// </summary>
public interface IEmbeddingService
{
    int Dimension { get; }
    float[] Embed(string text);
}

public class HashedNgramEmbeddings : IEmbeddingService
{
    public const int DefaultDim = 384;
    private readonly int _dim;

    public HashedNgramEmbeddings(int dimension = DefaultDim)
        => _dim = dimension > 0 ? dimension : DefaultDim;

    public int Dimension => _dim;

    private static readonly System.Text.RegularExpressions.Regex WordSplit =
        new(@"[\p{L}\p{Nd}]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public float[] Embed(string text)
    {
        var vec = new float[_dim];
        if (string.IsNullOrWhiteSpace(text)) return Normalize(vec);
        text = text.ToLowerInvariant();
        foreach (System.Text.RegularExpressions.Match m in WordSplit.Matches(text))
        {
            var w = m.Value;
            AddToken(vec, w);
            for (int n = 2; n <= 3; n++)
                for (int i = 0; i + n <= w.Length; i++)
                    AddToken(vec, w.AsSpan(i, n).ToString());
        }
        return Normalize(vec);
    }

    private static void AddToken(float[] vec, string token)
    {
        var h = (uint)StableHash(token);
        // 2 позиции на токен (double hashing)
        var idx1 = (int)(h % (uint)vec.Length);
        var idx2 = (int)((h >> 8 ^ 0x9E3779B9u) % (uint)vec.Length);
        vec[idx1] += Sign(h);
        vec[idx2] += Sign(~h);
    }

    private static float Sign(uint h) => ((h >> 16) & 1) == 0 ? 1f : -0.5f;

    private static int StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s) { h ^= ch; h *= 16777619; }
        return (int)h;
    }

    private static float[] Normalize(float[] v)
    {
        double norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm < 1e-9) return v;
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return v;
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na < 1e-12 || nb < 1e-12) return 0;
        return dot / Math.Sqrt(na * nb);
    }
}
