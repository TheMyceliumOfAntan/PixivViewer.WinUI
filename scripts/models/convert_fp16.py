"""把漫画翻译模型转换成 fp16（体积约减半）。

用法（需要 `pip install onnx onnxconverter-common`）：
    python scripts/models/convert_fp16.py <输入.onnx> <输出.onnx>

说明：
- 只做权重/激活的 fp16 转换，保留原始 I/O 类型，便于 ONNX Runtime 自动插入 cast。
- DirectML 支持 fp16，CPU 推理请继续使用 fp32 版本。
"""

import sys

import onnx
from onnxconverter_common import float16


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    source, target = sys.argv[1], sys.argv[2]
    model = onnx.load(source)

    # 部分算子对 fp16 敏感：LayerNorm/Softmax 等数值敏感，Resize 的 roi/scales 必须是 fp32
    converted = float16.convert_float_to_float16(
        model,
        keep_io_types=True,
        op_block_list=["Resize", "Upsample", "LayerNormalization", "Softmax", "ReduceMean", "TopK"],
    )

    onnx.save(converted, target)
    print(f"converted: {source} -> {target}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
