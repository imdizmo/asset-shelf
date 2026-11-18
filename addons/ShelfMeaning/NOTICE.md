# shelf-meaning: what it uses and under which licences

Checked 2026-11-18 at the sources named below.

## The model (downloaded on first use, not shipped)

- **Weights:** `onnx-community/siglip2-base-patch16-224-ONNX`, revision `ba1f3b0843f24bc5417d38e19c37b287d719b2f4`,
  files `onnx/vision_model.onnx` and `onnx/text_model.onnx`. That repository's card states no licence of its own; it
  names `base_model: google/siglip2-base-patch16-224` and describes itself as that model with ONNX weights. It is a
  format conversion of Google's weights, so Google's licence governs it.
- **Base model:** `google/siglip2-base-patch16-224` (Google's SigLIP 2), **Apache License 2.0** (its model card).
- **Tokenizer vocabulary:** `tokenizer.model`, SHA-256 `61a7b147390c64585d6c3543dd6fc636906c9af3865a5548f27f31aee1d4c8e2`.
  This is byte-identical to the `tokenizer.model` in `google/siglip2-base-patch16-224`, which Google publishes under
  Apache 2.0 with the model. (It is the Gemma family's SentencePiece vocabulary, as shipped by Google with SigLIP 2.)

The add-on downloads these from Hugging Face on the person's own machine and checks each by SHA-256. It does not
redistribute them. Anyone who redistributes the model files must include the Apache 2.0 licence with them.

## Libraries (shipped inside the add-on)

| Library | Version | Licence |
|---|---|---|
| Microsoft.ML.OnnxRuntime (and .Managed) | 1.32.0 | MIT (Microsoft); its package also carries ThirdPartyNotices.txt |
| Microsoft.ML.Tokenizers | 3.0.0 | MIT |
| SkiaSharp, SkiaSharp.NativeAssets.Linux | 3.119.4 | MIT; the native Skia library inside is BSD-3-Clause (Google) |

A release of the add-on puts the MIT texts, ONNX Runtime's ThirdPartyNotices.txt and Skia's BSD-3-Clause text
beside the program.

## Asset Shelf itself

Asset Shelf, ShelfIndex and `shelf` carry none of the above: no model code, no ONNX Runtime, no tokenizer library.
(Asset Shelf's window is drawn by Avalonia, which uses SkiaSharp for drawing only.)
