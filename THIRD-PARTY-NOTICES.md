# Third-party notices

Dialed Keyboard includes the following work by others.

## NK Cream key sounds (`DialedKeyboard/Sounds/nk-cream`)

From [Mechvibes](https://github.com/hainguyents13/mechvibes), used under the MIT License:

```
MIT License

Copyright (c) 2021 Hai Nguyen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## English word list (`DialedKeyboard/Data/words_en.txt`)

The `en_50k` (2018) list from [FrequencyWords](https://github.com/hermitdave/FrequencyWords)
by Hermit Dave, built from OpenSubtitles. It is licensed under
[CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) and is included unchanged.
That license covers this file only; the rest of Dialed Keyboard is MIT licensed (see `LICENSE`).

## NAudio

[NAudio](https://github.com/naudio/NAudio) (MIT License) plays the key sounds. NuGet downloads it
when the project is built, so it is not stored in this repository.

## U²-Net background removal model (`DialedKeyboard/Models/u2netp.onnx`)

The small "u2netp" model from [U²-Net](https://github.com/xuebinqin/U-2-Net) by Xuebin Qin et al.,
licensed under the [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0). The ONNX
file is the one published by [rembg](https://github.com/danielgatis/rembg) (MIT License) and is
included unchanged (SHA-256 `309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8`).

## ONNX Runtime

[ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT License) runs the background removal
model. NuGet downloads it when the project is built, so it is not stored in this repository.
