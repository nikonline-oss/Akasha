using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Akasha.Core.Services;

/// <summary>
/// Эмбеддинги через ONNX Runtime. Опционально: включается при наличии AKASHA_ONNX_MODEL.
/// Размерность берётся из самой модели (last hidden size), по умолчанию 384.
/// Поддерживает два токенизатора:
/// — WordPiece (<c>vocab.txt</c>) — BGE/bert/distiluse-модели;
/// — SentencePiece (<c>sentencepiece.bpe.model</c>) — XLM-R/мультиязычные модели.
/// При ошибке регистрации используется <see cref="HashedNgramEmbeddings"/> как fallback.
/// </summary>
public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    public const int DefaultDim = 384;

    private readonly InferenceSession _session;
    private readonly BertTokenizer? _wordPiece;
    private readonly SentencePieceTokenizer? _sentencePiece;
    private readonly int _dim;
    private readonly int _maxLength;
    private readonly string[] _inputNames;

    public int Dimension => _dim;

    /// <param name="dimension">0 — определить из модели (размер last hidden state).</param>
    public OnnxEmbeddingService(string modelPath, string vocabPath, int maxLength = 256, int dimension = 0)
    {
        _maxLength = Math.Clamp(maxLength, 8, 512);
        _session = new InferenceSession(modelPath);
        _inputNames = _session.InputMetadata.Keys.ToArray();
        if (!_inputNames.Contains("input_ids", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("ONNX-модель не содержит вход 'input_ids'.");

        _dim = dimension > 0 ? dimension : DetectDimension(_session);
        if (_dim <= 0) throw new InvalidOperationException("Не удалось определить размерность эмбеддингов.");

        // SentencePiece (*.model/*.spm) — XLM-R/мультиязычные модели; иначе WordPiece vocab.txt.
        if (vocabPath.EndsWith(".model", StringComparison.OrdinalIgnoreCase) ||
            vocabPath.EndsWith(".spm", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(vocabPath);
            _sentencePiece = SentencePieceTokenizer.Create(stream,
                addBeginningOfSentence: true, addEndOfSentence: true);
        }
        else
        {
            _wordPiece = BertTokenizer.Create(vocabPath, new BertOptions
            {
                LowerCaseBeforeTokenization = true,
                ApplyBasicTokenization = true,
                ClassificationToken = "[CLS]",
                SeparatorToken = "[SEP]",
                PaddingToken = "[PAD]",
                MaskingToken = "[MASK]",
            });
        }
    }

    /// <summary>Размер last hidden state из метаданных модели.</summary>
    public static int DetectDimension(string modelPath)
    {
        using var session = new InferenceSession(modelPath);
        return DetectDimension(session);
    }

    private static int DetectDimension(InferenceSession session)
    {
        foreach (var meta in session.OutputMetadata.Values)
        {
            var dims = meta.Dimensions;
            if (dims is { Length: >= 2 } && dims[^1] > 0) return dims[^1];
        }
        return DefaultDim;
    }

    public float[] Embed(string text)
    {
        var ids = _sentencePiece is not null
            ? _sentencePiece.EncodeToIds(text, addBeginningOfSentence: true, addEndOfSentence: true).ToList()
            : _wordPiece!.EncodeToIds(text, true, true).ToList();
        var len = Math.Min(ids.Count, _maxLength);
        var inputIds = new long[_maxLength];
        var attentionMask = new long[_maxLength];
        var tokenTypeIds = new long[_maxLength];
        for (int i = 0; i < len; i++)
        {
            inputIds[i] = ids[i];
            attentionMask[i] = 1;
        }

        var shape = new[] { 1, _maxLength };
        var inputs = new List<NamedOnnxValue>(3);
        foreach (var name in _inputNames)
        {
            if (string.Equals(name, "input_ids", StringComparison.OrdinalIgnoreCase))
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(inputIds, shape)));
            else if (string.Equals(name, "attention_mask", StringComparison.OrdinalIgnoreCase))
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(attentionMask, shape)));
            else if (string.Equals(name, "token_type_ids", StringComparison.OrdinalIgnoreCase))
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(tokenTypeIds, shape)));
        }

        using var results = _session.Run(inputs);
        var output = results[0].AsTensor<float>(); // [1, seq, dim]
        var dims = output.Dimensions;
        int seq = Math.Min(dims[1], _maxLength);
        int hidden = dims.Length > 2 ? dims[2] : _dim;

        var vec = new float[_dim];
        int counted = 0;
        for (int t = 0; t < seq; t++)
        {
            if (attentionMask[t] == 0) continue;
            for (int d = 0; d < _dim && d < hidden; d++)
                vec[d] += output[0, t, d];
            counted++;
        }
        if (counted > 0)
            for (int d = 0; d < _dim; d++) vec[d] /= counted;

        double norm = Math.Sqrt(vec.Sum(x => (double)x * x));
        if (norm > 1e-12)
            for (int d = 0; d < _dim; d++) vec[d] = (float)(vec[d] / norm);
        return vec;
    }

    public void Dispose() => _session.Dispose();
}
