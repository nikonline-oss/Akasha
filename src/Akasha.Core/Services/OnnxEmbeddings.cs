using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Akasha.Core.Services;

/// <summary>
/// Эмбеддинги через ONNX Runtime (384 dim). Опционально: включается при наличии AKASHA_ONNX_MODEL.
/// Поддерживает два токенизатора:
/// — WordPiece (<c>vocab.txt</c>) — BGE/bert-модели (например bge-small-en-v1.5);
/// — SentencePiece (<c>sentencepiece.bpe.model</c>) — мультиязычные XLM-R модели
///   (например paraphrase-multilingual-MiniLM-L12-v2), нужные для русского текста.
/// При ошибке регистрации используется <see cref="HashedNgramEmbeddings"/> как fallback.
/// </summary>
public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    public const int Dim = 384;
    public int Dimension => Dim;

    private readonly InferenceSession _session;
    private readonly BertTokenizer? _wordPiece;
    private readonly SentencePieceTokenizer? _sentencePiece;
    private readonly int _maxLength;
    private readonly string[] _inputNames;

    public OnnxEmbeddingService(string modelPath, string vocabPath, int maxLength = 256)
    {
        _maxLength = Math.Clamp(maxLength, 8, 512);
        _session = new InferenceSession(modelPath);
        _inputNames = _session.InputMetadata.Keys.ToArray();
        if (!_inputNames.Contains("input_ids", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("ONNX-модель не содержит вход 'input_ids'.");

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
        var output = results[0].AsTensor<float>(); // [1, seq, Dim]
        var dims = output.Dimensions;
        int seq = Math.Min(dims[1], _maxLength);
        int hidden = dims.Length > 2 ? dims[2] : Dim;

        var vec = new float[Dim];
        int counted = 0;
        for (int t = 0; t < seq; t++)
        {
            if (attentionMask[t] == 0) continue;
            for (int d = 0; d < Dim && d < hidden; d++)
                vec[d] += output[0, t, d];
            counted++;
        }
        if (counted > 0)
            for (int d = 0; d < Dim; d++) vec[d] /= counted;

        double norm = Math.Sqrt(vec.Sum(x => (double)x * x));
        if (norm > 1e-12)
            for (int d = 0; d < Dim; d++) vec[d] = (float)(vec[d] / norm);
        return vec;
    }

    public void Dispose() => _session.Dispose();
}
