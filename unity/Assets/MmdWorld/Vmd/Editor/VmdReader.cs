using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MmdWorld.Vmd
{
    /// <summary>
    /// VMD（Vocaloid Motion Data）を読む。ボーン・モーフ・表示/IK のキーを読み、カメラ・照明・セルフ影は数だけ数えて読み飛ばす。
    /// ボーン以降の区画は省略されていてもよい（古いファイルや、ボーンだけ書き出したファイル）。
    /// </summary>
    public static class VmdReader
    {
        const int BoneKeySize = 15 + 4 + 12 + 16 + 64;
        const int MorphKeySize = 15 + 4 + 4;
        const int CameraKeySize = 4 + 4 + 12 + 12 + 24 + 4 + 1;
        const int LightKeySize = 4 + 12 + 12;
        const int ShadowKeySize = 4 + 1 + 4;

        static Encoding _shiftJis;

        /// <summary>VMD の名前は Shift_JIS。</summary>
        public static Encoding ShiftJis => _shiftJis ?? (_shiftJis = Encoding.GetEncoding(932));

        public static VmdMotion Read(string path) => Read(File.ReadAllBytes(path));

        public static VmdMotion Read(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                var motion = new VmdMotion();

                string header = Encoding.ASCII.GetString(r.ReadBytes(30)).TrimEnd('\0');
                int modelNameLength;
                if (header.StartsWith("Vocaloid Motion Data 0002")) modelNameLength = 20;
                else if (header.StartsWith("Vocaloid Motion Data file")) modelNameLength = 10;
                else throw new InvalidDataException("VMD ではありません（ヘッダが違います）: " + header);
                motion.ModelName = ReadName(r, modelNameLength);

                int boneCount = ReadCount(r, BoneKeySize, "ボーン");
                for (int i = 0; i < boneCount; i++)
                {
                    string name = ReadName(r, 15);
                    var key = new VmdBoneKey
                    {
                        Frame = (int)r.ReadUInt32(),
                        Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                        Rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    };
                    byte[] ip = r.ReadBytes(64);
                    key.InterpX = Bezier(ip, 0);
                    key.InterpY = Bezier(ip, 1);
                    key.InterpZ = Bezier(ip, 2);
                    key.InterpR = Bezier(ip, 3);
                    key.Rotation = Normalize(key.Rotation);
                    Add(motion.Bones, name, key);
                }

                if (Remaining(r) < 4) return Done(motion);
                int morphCount = ReadCount(r, MorphKeySize, "モーフ");
                for (int i = 0; i < morphCount; i++)
                {
                    string name = ReadName(r, 15);
                    Add(motion.Morphs, name, new VmdMorphKey { Frame = (int)r.ReadUInt32(), Weight = r.ReadSingle() });
                }

                if (Remaining(r) < 4) return Done(motion);
                motion.CameraKeyCount = ReadCount(r, CameraKeySize, "カメラ");
                r.BaseStream.Seek((long)motion.CameraKeyCount * CameraKeySize, SeekOrigin.Current);

                if (Remaining(r) < 4) return Done(motion);
                int lightCount = ReadCount(r, LightKeySize, "照明");
                r.BaseStream.Seek((long)lightCount * LightKeySize, SeekOrigin.Current);

                if (Remaining(r) < 4) return Done(motion);
                int shadowCount = ReadCount(r, ShadowKeySize, "セルフ影");
                r.BaseStream.Seek((long)shadowCount * ShadowKeySize, SeekOrigin.Current);

                if (Remaining(r) < 4) return Done(motion);
                uint ikFrameCount = r.ReadUInt32();
                for (uint i = 0; i < ikFrameCount && Remaining(r) >= 9; i++)
                {
                    var key = new VmdIkKey { Frame = (int)r.ReadUInt32(), Enabled = new Dictionary<string, bool>() };
                    r.ReadByte(); // 表示
                    uint ikCount = r.ReadUInt32();
                    for (uint j = 0; j < ikCount; j++)
                    {
                        string name = ReadName(r, 20);
                        key.Enabled[name] = r.ReadByte() != 0;
                    }
                    motion.IkKeys.Add(key);
                }

                return Done(motion);
            }
        }

        static VmdMotion Done(VmdMotion motion)
        {
            motion.SortKeys();
            return motion;
        }

        static long Remaining(BinaryReader r) => r.BaseStream.Length - r.BaseStream.Position;

        static int ReadCount(BinaryReader r, int itemSize, string what)
        {
            uint count = r.ReadUInt32();
            if ((long)count * itemSize > Remaining(r))
                throw new InvalidDataException($"VMD の{what}の数（{count}）がファイルの残りに収まりません。壊れているかもしれません");
            return (int)count;
        }

        /// <summary>固定長の名前。0 の後ろは詰め物（0xFD など）なので捨てる。</summary>
        static string ReadName(BinaryReader r, int length)
        {
            byte[] bytes = r.ReadBytes(length);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            return DecodeName(bytes, end);
        }

        static string DecodeName(byte[] bytes, int length)
        {
            // 名前の途中で切れた2バイト文字は捨てる
            string s = ShiftJis.GetString(bytes, 0, length);
            return s.TrimEnd('�');
        }

        /// <summary>補間パラメータは 4 チャンネル × (x1, y1, x2, y2) が列方向に並んでいる。</summary>
        static VmdBezier Bezier(byte[] ip, int channel) => new VmdBezier
        {
            X1 = ip[channel + 0] / 127f,
            Y1 = ip[channel + 4] / 127f,
            X2 = ip[channel + 8] / 127f,
            Y2 = ip[channel + 12] / 127f,
        };

        static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m < 1e-6f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        static void Add<T>(Dictionary<string, List<T>> dict, string name, T key)
        {
            if (!dict.TryGetValue(name, out var list)) dict[name] = list = new List<T>();
            list.Add(key);
        }
    }
}
