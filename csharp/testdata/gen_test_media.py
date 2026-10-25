"""生成两个短 wav 供 clip-switch 测试（stdlib only）。"""
import math, struct, wave

def make(path, sec, freq):
    with wave.open(path, "w") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000)
        frames = b"".join(
            struct.pack("<h", int(8000 * math.sin(2 * math.pi * freq * i / 16000)))
            for i in range(int(16000 * sec)))
        w.writeframes(frames)

make("a.wav", 6, 440)
make("b.wav", 6, 660)
print("ok")
