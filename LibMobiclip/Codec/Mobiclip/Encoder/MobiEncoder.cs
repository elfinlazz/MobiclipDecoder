﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;

namespace LibMobiclip.Codec.Mobiclip.Encoder
{
    public class MobiEncoder
    {
        public enum FrameType
        {
            None,
            Intra,
            Prediction
        }

        public MobiEncoder(int Quantizer, int Width, int Height)
        {
            if (Width % 16 != 0 || Height % 16 != 0) throw new Exception();
            if (Quantizer < 0xC) Quantizer = 0xC;
            if (Quantizer > 0x34) Quantizer = 0x34;
            this.Quantizer = Quantizer;
            this.Width = Width;
            this.Height = Height;
            Stride = 1024;
            if (Width <= 256) Stride = 256;
            else if (Width <= 512) Stride = 512;
            YDec = new byte[Stride * Height];
            UVDec = new byte[Stride * Height / 2];
            MacroBlocks = new MacroBlock[Height / 16][];
            for (int i = 0; i < Height / 16; i++)
            {
                MacroBlocks[i] = new MacroBlock[Width / 16];
            }
            LastFrameType = FrameType.None;
            SetupQuantizationTables();
        }

        public FrameType LastFrameType { get; private set; }
        public int Quantizer { get; private set; }

        public int Width { get; private set; }
        public int Height { get; private set; }

        public float[] QTable4x4 { get; private set; }
        public float[] QTable8x8 { get; private set; }

        public byte[] YDec { get; private set; }
        public byte[] UVDec { get; private set; }
        public int Stride { get; private set; }

        public MacroBlock[][] MacroBlocks { get; private set; }

        private Bitmap[] PastFrames = new Bitmap[6];

        private int NrPFrames = 0;

        public byte[] EncodeFrame(Bitmap Frame)
        {
            for (int i = 5; i > 0; i--)
            {
                PastFrames[i] = PastFrames[i - 1];
            }
            PastFrames[0] = Frame;
            //TODO: I or P
            FrameType thistype = FrameType.Intra;
            if (LastFrameType != FrameType.None && NrPFrames < 30)
                thistype = FrameType.Prediction;
            LastFrameType = thistype;
            if (thistype == FrameType.Intra)
            {
                NrPFrames = 0;
                return EncodeIntra(Frame);
            }
            else if (thistype == FrameType.Prediction)
            {
                NrPFrames++;
                return EncodePrediction(Frame);
            }
            return null;
        }

        private unsafe byte[] EncodePrediction(Bitmap Frame)
        {
            //We'll just look if there are blocks that are 100% the same first
            BitmapData d = Frame.LockBits(new Rectangle(0, 0, Frame.Width, Frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData prev = PastFrames[1].LockBits(new Rectangle(0, 0, Frame.Width, Frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < Height; y += 16)
            {
                for (int x = 0; x < Width; x += 16)
                {
                    MacroBlocks[y / 16][x / 16] = new MacroBlock(d, x, y);
                    bool same = true;
                    for (int y2 = 0; y2 < 16; y2++)
                    {
                        ulong* pA = (ulong*)(((byte*)d.Scan0) + (y + y2) * d.Stride + x * 4);
                        ulong* pB = (ulong*)(((byte*)prev.Scan0) + (y + y2) * prev.Stride + x * 4);
                        for (int x2 = 0; x2 < 16; x2+=2)
                        {
                            ulong color = *pA++;
                            ulong color2 = *pB++;
                            if (color != color2)
                            {
                                same = false;
                                break;
                            }
                        }
                        if (!same) break;
                    }
                    if (same) MacroBlocks[y / 16][x / 16].Predict = true;
                    else
                    {
                        Analyzer.ConfigureBlockY(this, MacroBlocks[y / 16][x / 16]);
                        int blocktype2 = Analyzer.AnalyzeBlockUV(this, MacroBlocks[y / 16][x / 16]);
                        MacroBlocks[y / 16][x / 16].UVPredictionMode = blocktype2;
                        MacroBlocks[y / 16][x / 16].UVUseComplex8x8[0] = true;
                        MacroBlocks[y / 16][x / 16].UVUseComplex8x8[1] = true;
                        MacroBlocks[y / 16][x / 16].SetupDCTs(this);
                    }
                }
            }
            Frame.UnlockBits(d);
            PastFrames[1].UnlockBits(prev);
            BitWriter b = new BitWriter();
            b.WriteBits(0, 1);//Interframe
            b.WriteVarIntSigned(0);
            for (int y = 0; y < Height; y += 16)
            {
                for (int x = 0; x < Width; x += 16)
                {
                    MacroBlock curblock = MacroBlocks[y / 16][x / 16];
                    if (curblock.Predict)
                    {
                        b.WriteBits(1, 1);
                        b.WriteVarIntUnsigned(0);
                    }
                    else
                    {
                        b.WriteBits(0xE >> 1, 5);
                        EncodeBlockIntra(curblock, b);
                    }
                }
            }
            b.WriteBits(0, 16);
            b.Flush();
            byte[] result = b.ToArray();
            return result;
        }

        private byte[] REV_byte_115FC4 =
        {
            0, 7, 6, 12, 5, 19, 29, 13, 4, 27, 17, 8, 14, 11, 9, 3, 20, 33, 34, 24, 35, 30, 41, 15, 36, 40, 31, 10, 28, 16, 18, 1, 37, 55, 57, 52, 58, 56, 63, 46, 61, 62, 60, 48, 59, 49, 54, 21, 43, 44, 45, 32, 47, 39, 53, 22, 51, 50, 42, 23, 38, 25, 26, 2, 
        };

        private byte[] REV_byte_1164F4 =
        {
	        2, 4, 3, 8, 5, 14, 16, 12, 6, 15, 13, 9, 7, 10, 11, 1
        };

        private byte[] EncodeIntra(Bitmap Frame)
        {
            BitmapData d = Frame.LockBits(new Rectangle(0, 0, Frame.Width, Frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            //Setup the macroblocks
            for (int y = 0; y < Height; y += 16)
            {
                for (int x = 0; x < Width; x += 16)
                {
                    MacroBlocks[y / 16][x / 16] = new MacroBlock(d, x, y);
                    Analyzer.ConfigureBlockY(this, MacroBlocks[y / 16][x / 16]);
                    int blocktype2 = Analyzer.AnalyzeBlockUV(this, MacroBlocks[y / 16][x / 16]);
                    MacroBlocks[y / 16][x / 16].UVPredictionMode = blocktype2;
                    MacroBlocks[y / 16][x / 16].UVUseComplex8x8[0] = true;
                    MacroBlocks[y / 16][x / 16].UVUseComplex8x8[1] = true;
                    MacroBlocks[y / 16][x / 16].SetupDCTs(this);
                }
            }
            Frame.UnlockBits(d);
            BitWriter b = new BitWriter();
            b.WriteBits(1, 1);//Interframe
            b.WriteBits(1, 1);//YUV format
            //TODO: determine table (when we actually use it...)
            b.WriteBits(0, 1);//Table
            b.WriteBits((uint)Quantizer, 6);//Quantizer
            for (int y = 0; y < Height; y += 16)
            {
                for (int x = 0; x < Width; x += 16)
                {
                    MacroBlock curblock = MacroBlocks[y / 16][x / 16];

                    //Todo use predicted prediction mode
                    /*if (x > 0 && y > 0)
                    {
                        int r12 = MacroBlocks[y / 16 - 1][x / 16].YPredictionMode;
                        int r6 = MacroBlocks[y / 16][x / 16 - 1].YPredictionMode;
                        if (r12 > r6) r12 = r6;
                        if (r12 == 9) r12 = 3;
                        r6 = r3 >> 28;
                        if (r6 >= r12) r6++;
                        int r7;
                        if (r6 < 9)
                        {
                            r12 = r6;
                            r7 = 4;
                        }
                        else r7 = 1;
                        r3 <<= r7;
                        nrBitsRemaining -= r7;
                    }*/


                    b.WriteBits(0, 1);//Func
                    EncodeBlockIntra(curblock, b);
                }
            }
            b.WriteBits(0, 16);
            b.Flush();
            byte[] result = b.ToArray();
            return result;
        }

        private void EncodeBlockIntra(MacroBlock Block, BitWriter b)
        {
            uint dctmask =
                       (Block.YUseComplex8x8[0] ? 1u : 0) |
                       ((Block.YUseComplex8x8[1] ? 1u : 0) << 1) |
                       ((Block.YUseComplex8x8[2] ? 1u : 0) << 2) |
                       ((Block.YUseComplex8x8[3] ? 1u : 0) << 3) |
                       ((Block.UVUseComplex8x8[0] ? 1u : 0) << 4) |
                       ((Block.UVUseComplex8x8[1] ? 1u : 0) << 5);
            b.WriteVarIntUnsigned(REV_byte_115FC4[dctmask]);
            b.WriteBits((uint)Block.YPredictionMode, 3);//Block type
            for (int y2 = 0; y2 < 2; y2++)
            {
                for (int x2 = 0; x2 < 2; x2++)
                {
                    if (Block.YUseComplex8x8[x2 + y2 * 2] && !Block.YUse4x4[x2 + y2 * 2])
                    {
                        b.WriteBits(1, 1);//Don't use 4x4 blocks
                        EncodeDCT(Block.YDCT8x8[x2 + y2 * 2], 0, b);
                    }
                    else if (Block.YUseComplex8x8[x2 + y2 * 2] && Block.YUse4x4[x2 + y2 * 2])
                    {
                        uint dctmask2 =
                            (Block.YUseDCT4x4[x2 + y2 * 2][0] ? 1u : 0) |
                            ((Block.YUseDCT4x4[x2 + y2 * 2][1] ? 1u : 0) << 1) |
                            ((Block.YUseDCT4x4[x2 + y2 * 2][2] ? 1u : 0) << 2) |
                            ((Block.YUseDCT4x4[x2 + y2 * 2][3] ? 1u : 0) << 3);
                        b.WriteVarIntUnsigned(REV_byte_1164F4[dctmask2]);
                        for (int y3 = 0; y3 < 2; y3++)
                        {
                            for (int x3 = 0; x3 < 2; x3++)
                            {
                                if (Block.YUseDCT4x4[x2 + y2 * 2][x3 + y3 * 2])
                                    EncodeDCT(Block.YDCT4x4[x2 + y2 * 2][x3 + y3 * 2], 0, b);
                            }
                        }
                    }
                }
            }
            b.WriteBits((uint)Block.UVPredictionMode, 3);//Block type
            for (int q = 0; q < 2; q++)
            {
                if (Block.UVUseComplex8x8[q] && !Block.UVUse4x4[q])
                {
                    b.WriteBits(1, 1);//Don't use 4x4 blocks
                    EncodeDCT(Block.UVDCT8x8[q], 0, b);
                }
            }
        }

        private static int CLZ(uint value)
        {
            int leadingZeros = 0;
            while (value != 0)
            {
                value = value >> 1;
                leadingZeros++;
            }

            return (32 - leadingZeros);
        }

        private void EncodeDCT(int[] DCT, int Table, BitWriter b)
        {
            ushort[] r11A = (Table == 1 ? MobiConst.Vx2Table1_A : MobiConst.Vx2Table0_A);
            byte[] r11B = (Table == 1 ? MobiConst.Vx2Table1_B : MobiConst.Vx2Table0_B);
            int lastnonzero = 0;
            for (int i = 0; i < DCT.Length; i++)
            {
                if (DCT[i] != 0) lastnonzero = i;
            }
            int skip = 0;
            for (int i = 0; i < DCT.Length; i++)
            {
                if (DCT[i] == 0 && lastnonzero != 0)
                {
                    skip++;
                    continue;
                }
                int val = DCT[i];
                if (val < 0) val = -val;
                if (val <= 31)
                {
                    int idx = MobiConst.VxTable0_A_Ref[val, skip, (i == lastnonzero) ? 1 : 0];
                    if (idx >= 0)
                    {
                        int nrbits = (r11A[idx] & 0xF);
                        uint tidx = (uint)idx;
                        if (nrbits < 12)
                            tidx >>= (12 - nrbits);
                        else if (nrbits > 12)
                            tidx <<= (nrbits - 12);
                        if (DCT[i] < 0) tidx |= 1;
                        b.WriteBits((uint)tidx, nrbits);
                        skip = 0;
                        goto end;
                    }
                    int newskip = skip - MobiConst.Vx2Table0_B[(val | (((i == lastnonzero) ? 1 : 0) << 6)) + 0x80];
                    if (newskip >= 0)
                    {
                        idx = MobiConst.VxTable0_A_Ref[val, newskip, (i == lastnonzero) ? 1 : 0];
                        if (idx >= 0)
                        {
                            b.WriteBits(3, 7);
                            b.WriteBits(1, 1);
                            b.WriteBits(0, 1);
                            int nrbits = (r11A[idx] & 0xF);
                            uint tidx = (uint)idx;
                            if (nrbits < 12)
                                tidx >>= (12 - nrbits);
                            else if (nrbits > 12)
                                tidx <<= (nrbits - 12);
                            if (DCT[i] < 0) tidx |= 1;
                            b.WriteBits((uint)tidx, nrbits);
                            skip = 0;
                            goto end;
                        }
                    }
                }
                int newval = val - MobiConst.Vx2Table0_B[skip | (((i == lastnonzero) ? 1 : 0) << 6)];
                if (newval >= 0 && newval <= 31)
                {
                    int idx = MobiConst.VxTable0_A_Ref[newval, skip, (i == lastnonzero) ? 1 : 0];
                    if (idx >= 0)
                    {
                        b.WriteBits(3, 7);
                        b.WriteBits(0, 1);
                        int nrbits = (r11A[idx] & 0xF);
                        uint tidx = (uint)idx;
                        if (nrbits < 12)
                            tidx >>= (12 - nrbits);
                        else if (nrbits > 12)
                            tidx <<= (nrbits - 12);
                        if (DCT[i] < 0) tidx |= 1;
                        b.WriteBits((uint)tidx, nrbits);
                        skip = 0;
                        goto end;
                    }
                }
                //This is easiest way of writing the DCT, but also costs the most bits
                b.WriteBits(3, 7);
                b.WriteBits(1, 1);
                b.WriteBits(1, 1);
                if (i == lastnonzero) b.WriteBits(1, 1);
                else b.WriteBits(0, 1);
                b.WriteBits((uint)skip, 6);
                skip = 0;
                b.WriteBits((uint)DCT[i], 12);
            end:
                if (i == lastnonzero) break;
            }
        }

        private static byte[] byte_118DD4 = 
        {
	        0x14, 0x13, 0x13, 0x19, 0x12, 0x19, 0x13, 0x18, 0x18, 0x13, 0x14, 0x12,
	        0x20, 0x12, 0x14, 0x13, 0x13, 0x18, 0x18, 0x13, 0x13, 0x19, 0x12, 0x19,
	        0x12, 0x19, 0x12, 0x19, 0x13, 0x18, 0x18, 0x13, 0x13, 0x18, 0x18, 0x13,
	        0x12, 0x20, 0x12, 0x14, 0x12, 0x20, 0x12, 0x18, 0x18, 0x13, 0x13, 0x18,
	        0x18, 0x12, 0x19, 0x12, 0x19, 0x12, 0x13, 0x18, 0x18, 0x13, 0x12, 0x20,
	        0x12, 0x18, 0x18, 0x12, 0x16, 0x15, 0x15, 0x1C, 0x13, 0x1C, 0x15, 0x1A,
	        0x1A, 0x15, 0x16, 0x13, 0x23, 0x13, 0x16, 0x15, 0x15, 0x1A, 0x1A, 0x15,
	        0x15, 0x1C, 0x13, 0x1C, 0x13, 0x1C, 0x13, 0x1C, 0x15, 0x1A, 0x1A, 0x15,
	        0x15, 0x1A, 0x1A, 0x15, 0x13, 0x23, 0x13, 0x16, 0x13, 0x23, 0x13, 0x1A,
	        0x1A, 0x15, 0x15, 0x1A, 0x1A, 0x13, 0x1C, 0x13, 0x1C, 0x13, 0x15, 0x1A,
	        0x1A, 0x15, 0x13, 0x23, 0x13, 0x1A, 0x1A, 0x13, 0x1A, 0x18, 0x18, 0x21,
	        0x17, 0x21, 0x18, 0x1F, 0x1F, 0x18, 0x1A, 0x17, 0x2A, 0x17, 0x1A, 0x18,
	        0x18, 0x1F, 0x1F, 0x18, 0x18, 0x21, 0x17, 0x21, 0x17, 0x21, 0x17, 0x21,
	        0x18, 0x1F, 0x1F, 0x18, 0x18, 0x1F, 0x1F, 0x18, 0x17, 0x2A, 0x17, 0x1A,
	        0x17, 0x2A, 0x17, 0x1F, 0x1F, 0x18, 0x18, 0x1F, 0x1F, 0x17, 0x21, 0x17,
	        0x21, 0x17, 0x18, 0x1F, 0x1F, 0x18, 0x17, 0x2A, 0x17, 0x1F, 0x1F, 0x17,
	        0x1C, 0x1A, 0x1A, 0x23, 0x19, 0x23, 0x1A, 0x21, 0x21, 0x1A, 0x1C, 0x19,
	        0x2D, 0x19, 0x1C, 0x1A, 0x1A, 0x21, 0x21, 0x1A, 0x1A, 0x23, 0x19, 0x23,
	        0x19, 0x23, 0x19, 0x23, 0x1A, 0x21, 0x21, 0x1A, 0x1A, 0x21, 0x21, 0x1A,
	        0x19, 0x2D, 0x19, 0x1C, 0x19, 0x2D, 0x19, 0x21, 0x21, 0x1A, 0x1A, 0x21,
	        0x21, 0x19, 0x23, 0x19, 0x23, 0x19, 0x1A, 0x21, 0x21, 0x1A, 0x19, 0x2D,
	        0x19, 0x21, 0x21, 0x19, 0x20, 0x1E, 0x1E, 0x28, 0x1C, 0x28, 0x1E, 0x26,
	        0x26, 0x1E, 0x20, 0x1C, 0x33, 0x1C, 0x20, 0x1E, 0x1E, 0x26, 0x26, 0x1E,
	        0x1E, 0x28, 0x1C, 0x28, 0x1C, 0x28, 0x1C, 0x28, 0x1E, 0x26, 0x26, 0x1E,
	        0x1E, 0x26, 0x26, 0x1E, 0x1C, 0x33, 0x1C, 0x20, 0x1C, 0x33, 0x1C, 0x26,
	        0x26, 0x1E, 0x1E, 0x26, 0x26, 0x1C, 0x28, 0x1C, 0x28, 0x1C, 0x1E, 0x26,
	        0x26, 0x1E, 0x1C, 0x33, 0x1C, 0x26, 0x26, 0x1C, 0x24, 0x22, 0x22, 0x2E,
	        0x20, 0x2E, 0x22, 0x2B, 0x2B, 0x22, 0x24, 0x20, 0x3A, 0x20, 0x24, 0x22,
	        0x22, 0x2B, 0x2B, 0x22, 0x22, 0x2E, 0x20, 0x2E, 0x20, 0x2E, 0x20, 0x2E,
	        0x22, 0x2B, 0x2B, 0x22, 0x22, 0x2B, 0x2B, 0x22, 0x20, 0x3A, 0x20, 0x24,
	        0x20, 0x3A, 0x20, 0x2B, 0x2B, 0x22, 0x22, 0x2B, 0x2B, 0x20, 0x2E, 0x20,
	        0x2E, 0x20, 0x22, 0x2B, 0x2B, 0x22, 0x20, 0x3A, 0x20, 0x2B, 0x2B, 0x20
        };

        private static byte[] byte_119004 =
        {
	        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
	        0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
	        0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
	        0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
	        0x08, 0x08, 0x08, 0x08, 0x08, 0x08
        };

        private static byte[] byte_11903A = 
        {
	        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05,
	        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05,
	        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05,
	        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05,
	        0x00, 0x01, 0x02, 0x03, 0x04, 0x05
        };



        private byte[] byte_118F94 =
        {
	        0x0A, 0x0D, 0x0D, 0x0A, 0x10, 0x0A, 0x0D, 0x0D, 0x0D, 0x0D, 0x10, 0x0A,
	        0x10, 0x0D, 0x0D, 0x10, 0x0B, 0x0E, 0x0E, 0x0B, 0x12, 0x0B, 0x0E, 0x0E,
	        0x0E, 0x0E, 0x12, 0x0B, 0x12, 0x0E, 0x0E, 0x12, 0x0D, 0x10, 0x10, 0x0D,
	        0x14, 0x0D, 0x10, 0x10, 0x10, 0x10, 0x14, 0x0D, 0x14, 0x10, 0x10, 0x14,
	        0x0E, 0x12, 0x12, 0x0E, 0x17, 0x0E, 0x12, 0x12, 0x12, 0x12, 0x17, 0x0E,
	        0x17, 0x12, 0x12, 0x17, 0x10, 0x14, 0x14, 0x10, 0x19, 0x10, 0x14, 0x14,
	        0x14, 0x14, 0x19, 0x10, 0x19, 0x14, 0x14, 0x19, 0x12, 0x17, 0x17, 0x12,
	        0x1D, 0x12, 0x17, 0x17, 0x17, 0x17, 0x1D, 0x12, 0x1D, 0x17, 0x17, 0x1D
        };

        private void SetupQuantizationTables()
        {
            float[] Table = new float[16];
            int r6 = byte_119004[Quantizer] + 8;
            int r5 = byte_11903A[Quantizer];
            int r4 = r5 << 4;
            for (int i = 0; i < 16; i++)
            {
                Table[i] = (byte_118F94[r4++] << r6) >> 8;
            }
            //Dezigzag
            QTable4x4 = new float[16];
            for (int i = 0; i < 16; i++)
            {
                QTable4x4[MobiConst.DeZigZagTable4x4[i]] = Table[i];
            }

            Table = new float[64];
            r6 -= 2;
            r4 = r5 << 6;
            for (int i = 0; i < 64; i++)
            {
                Table[i] = (byte_118DD4[r4++] << r6) >> 8;
            }
            //Dezigzag
            QTable8x8 = new float[64];
            for (int i = 0; i < 64; i++)
            {
                QTable8x8[MobiConst.DeZigZagTable8x8[i]] = Table[i];
            }
        }

        public static int[] DCT64(int[] InPixels)
        {
            int[] Pixels = new int[64];
            for (int i = 0; i < 64; i++)
            {
                Pixels[i] = InPixels[i] * 64;
            }
            int[] tmp = new int[64];
            for (int i = 0; i < 8; i++)
            {
                int p = Pixels[i * 8 + 0];
                int q = Pixels[i * 8 + 7];
                int r = Pixels[i * 8 + 2];
                int s = Pixels[i * 8 + 5];
                int t = Pixels[i * 8 + 3];
                int u = Pixels[i * 8 + 4];
                int v = Pixels[i * 8 + 1];
                int w = Pixels[i * 8 + 6];
                tmp[i * 8 + 0] = (w + v + u + t + s + r + q + p) / 8;
                tmp[i * 8 + 1] = (-40 * w + 40 * v - 12 * u + 12 * t - 24 * s + 24 * r - 48 * q + 48 * p) / 289;
                tmp[i * 8 + 2] = (w + v - 2 * u - 2 * t - s - r + 2 * q + 2 * p) / 10;
                tmp[i * 8 + 3] = (12 * w - 12 * v + 24 * u - 24 * t + 48 * s - 48 * r - 40 * q + 40 * p) / 289;
                tmp[i * 8 + 4] = (-w - v + u + t - s - r + q + p) / 8;
                tmp[i * 8 + 5] = (48 * w - 48 * v - 40 * u + 40 * t - 12 * s + 12 * r - 24 * q + 24 * p) / 289;
                tmp[i * 8 + 6] = (-2 * w - 2 * v - u - t + 2 * s + 2 * r + q + p) / 10;
                tmp[i * 8 + 7] = (24 * w - 24 * v + 48 * u - 48 * t - 40 * s + 40 * r - 12 * q + 12 * p) / 289;
            }
            int[] tmp2 = new int[64];
            for (int i = 0; i < 8; i++)
            {
                int p = tmp[0 * 8 + i];
                int q = tmp[7 * 8 + i];
                int r = tmp[2 * 8 + i];
                int s = tmp[5 * 8 + i];
                int t = tmp[3 * 8 + i];
                int u = tmp[4 * 8 + i];
                int v = tmp[1 * 8 + i];
                int w = tmp[6 * 8 + i];
                tmp2[i * 8 + 0] = (w + v + u + t + s + r + q + p) / 8;
                tmp2[i * 8 + 1] = (-40 * w + 40 * v - 12 * u + 12 * t - 24 * s + 24 * r - 48 * q + 48 * p) / 289;
                tmp2[i * 8 + 2] = (w + v - 2 * u - 2 * t - s - r + 2 * q + 2 * p) / 10;
                tmp2[i * 8 + 3] = (12 * w - 12 * v + 24 * u - 24 * t + 48 * s - 48 * r - 40 * q + 40 * p) / 289;
                tmp2[i * 8 + 4] = (-w - v + u + t - s - r + q + p) / 8;
                tmp2[i * 8 + 5] = (48 * w - 48 * v - 40 * u + 40 * t - 12 * s + 12 * r - 24 * q + 24 * p) / 289;
                tmp2[i * 8 + 6] = (-2 * w - 2 * v - u - t + 2 * s + 2 * r + q + p) / 10;
                tmp2[i * 8 + 7] = (24 * w - 24 * v + 48 * u - 48 * t - 40 * s + 40 * r - 12 * q + 12 * p) / 289;
            }
            return tmp2;
        }

        public static byte[] IDCT64(int[] DCT, byte[] PPixels)
        {
            int lr = 0;
            int r11 = 0;// lr + 64;
            int r0 = (int)DCT[lr++];
            int r1 = (int)DCT[lr++];
            int r2 = (int)DCT[lr++];
            int r3 = (int)DCT[lr++];
            int r4 = (int)DCT[lr++];
            int r5 = (int)DCT[lr++];
            int r6 = (int)DCT[lr++];
            int r7 = (int)DCT[lr++];
            int r8, r9;
            r0 += 0x20;

            int[] DCTtmp = new int[64];

            int r12 = 8;
            while (true)
            {
                r8 = r0 + r4;
                r9 = r0 - r4;
                r0 = r2 + (r6 >> 1);
                r4 = (r2 >> 1) - r6;
                r2 = r9 + r4;
                r4 = r9 - r4;
                r6 = r8 - r0;
                r0 = r8 + r0;
                r8 = r1 + r7;
                r8 -= r3;
                r8 -= (r3 >> 1);
                r9 = r7 - r1;
                r9 += r5;
                r9 += (r5 >> 1);
                r7 += (r7 >> 1);
                r7 = r5 - r7;
                r7 -= r3;
                r3 += r5;
                r3 += r1;
                r3 += (r1 >> 1);
                r1 = r7 + (r3 >> 2);
                r7 = r3 - (r7 >> 2);
                r3 = r8 + (r9 >> 2);
                r5 = (r8 >> 2) - r9;
                r0 += r7;
                r7 = r0 - r7 * 2;
                r8 = r2 + r5;
                r9 = r2 - r5;
                r2 = r4 + r3;
                r5 = r4 - r3;
                r3 = r6 + r1;
                r4 = r6 - r1;
                r1 = r8;
                r6 = r9;
                DCTtmp[r11 + 56] = r7;
                DCTtmp[r11 + 48] = r6;
                DCTtmp[r11 + 40] = r5;
                DCTtmp[r11 + 32] = r4;
                DCTtmp[r11 + 24] = r3;
                DCTtmp[r11 + 16] = r2;
                DCTtmp[r11 + 8] = r1;
                DCTtmp[r11 + 0] = r0;
                r11++;
                r12--;
                if (r12 <= 0) break;
                r0 = (int)DCT[lr++];
                r1 = (int)DCT[lr++];
                r2 = (int)DCT[lr++];
                r3 = (int)DCT[lr++];
                r4 = (int)DCT[lr++];
                r5 = (int)DCT[lr++];
                r6 = (int)DCT[lr++];
                r7 = (int)DCT[lr++];
            }
            r11 -= 8;
            byte[] result = new byte[64];
            int Offset = 0;
            for (int i = 0; i < 8; i++)
            {
                r0 = (int)DCTtmp[r11++];
                r1 = (int)DCTtmp[r11++];
                r2 = (int)DCTtmp[r11++];
                r3 = (int)DCTtmp[r11++];
                r4 = (int)DCTtmp[r11++];
                r5 = (int)DCTtmp[r11++];
                r6 = (int)DCTtmp[r11++];
                r7 = (int)DCTtmp[r11++];
                r9 = r0 + r4;
                int r10 = r0 - r4;
                r0 = r2 + (r6 >> 1);
                r4 = (r2 >> 1) - r6;
                r2 = r10 + r4;
                r4 = r10 - r4;
                r6 = r9 - r0;
                r0 = r9 + r0;
                r9 = r1 + r7;
                r9 -= r3;
                r9 -= (r3 >> 1);
                r10 = r7 - r1;
                r10 += r5;
                r10 += (r5 >> 1);
                r7 += (r7 >> 1);
                r7 = r5 - r7;
                r7 -= r3;
                r3 += r5;
                r3 += r1;
                r3 += (r1 >> 1);
                r1 = r7 + (r3 >> 2);
                r7 = r3 - (r7 >> 2);
                r3 = r9 + (r10 >> 2);
                r5 = (r9 >> 2) - r10;
                r0 += r7;
                r7 = r0 - r7 * 2;
                r9 = r2 + r5;
                r10 = r2 - r5;
                r2 = r4 + r3;
                r5 = r4 - r3;
                r3 = r6 + r1;
                r4 = r6 - r1;
                r1 = r9;
                r6 = r10;
                result[Offset + 0] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 0] + (r0 >> 6)];
                result[Offset + 1] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 1] + (r1 >> 6)];
                result[Offset + 2] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 2] + (r2 >> 6)];
                result[Offset + 3] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 3] + (r3 >> 6)];
                result[Offset + 4] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 4] + (r4 >> 6)];
                result[Offset + 5] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 5] + (r5 >> 6)];
                result[Offset + 6] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 6] + (r6 >> 6)];
                result[Offset + 7] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 7] + (r7 >> 6)];
                Offset += 8;//Stride;
            }
            return result;
        }

        public static int[] DCT16(int[] InPixels)
        {
            int[] Pixels = new int[16];
            for (int i = 0; i < 16; i++)
            {
                Pixels[i] = InPixels[i] * 64;
            }
            int[] tmp = new int[16];
            for (int i = 0; i < 4; i++)
            {
                int q = Pixels[i * 4 + 0];
                int r = Pixels[i * 4 + 1];
                int s = Pixels[i * 4 + 2];
                int t = Pixels[i * 4 + 3];
                tmp[i * 4 + 0] = (t + s + r + q) / 4;
                tmp[i * 4 + 1] = (-2 * t - s + r + 2 * q) / 5;
                tmp[i * 4 + 2] = (t - s - r + q) / 4;
                tmp[i * 4 + 3] = (-t + 2 * s - 2 * r + q) / 5;
            }
            int[] tmp2 = new int[16];
            for (int i = 0; i < 4; i++)
            {
                int q = tmp[0 * 4 + i];
                int r = tmp[1 * 4 + i];
                int s = tmp[2 * 4 + i];
                int t = tmp[3 * 4 + i];
                tmp2[i * 4 + 0] = (t + s + r + q) / 4;
                tmp2[i * 4 + 1] = (-2 * t - s + r + 2 * q) / 5;
                tmp2[i * 4 + 2] = (t - s - r + q) / 4;
                tmp2[i * 4 + 3] = (-t + 2 * s - 2 * r + q) / 5;
            }
            return tmp2;
        }

        public static byte[] IDCT16(int[] DCT, byte[] PPixels)
        {
            int lr = 0;// 90;
            int r11 = 0;// lr + 16;
            int r0 = (int)DCT[lr++];
            int r1 = (int)DCT[lr++];
            int r2 = (int)DCT[lr++];
            int r3 = (int)DCT[lr++];
            r0 += 0x20;

            int[] DCTtmp = new int[16];

            int r12 = 4;
            while (true)
            {
                r0 += r2;
                r2 = r0 - r2 * 2;
                int r8 = (r1 >> 1) - r3;
                int r9 = r1 + (r3 >> 1);
                r3 = r0 - r9;
                r0 += r9;
                r1 = r2 + r8;
                r2 -= r8;
                DCTtmp[r11 + 12] = r3;
                DCTtmp[r11 + 8] = r2;
                DCTtmp[r11 + 4] = r1;
                DCTtmp[r11 + 0] = r0;
                r11++;
                r12--;
                if (r12 <= 0) break;
                r0 = (int)DCT[lr++];
                r1 = (int)DCT[lr++];
                r2 = (int)DCT[lr++];
                r3 = (int)DCT[lr++];
            }
            r11 -= 4;
            r12 = 4;
            byte[] result = new byte[16];
            int Offset = 0;
            while (true)
            {
                r0 = DCTtmp[r11++];
                r1 = DCTtmp[r11++];
                r2 = DCTtmp[r11++];
                r3 = DCTtmp[r11++];
                r0 += r2;
                r2 = r0 - r2 * 2;
                int r9 = (r1 >> 1) - r3;
                int r10 = r1 + (r3 >> 1);
                r3 = r0 - r10;
                r0 += r10;
                r1 = r2 + r9;
                r2 -= r9;
                result[Offset + 0] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 0] + (r0 >> 6)];
                result[Offset + 1] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 1] + (r1 >> 6)];
                result[Offset + 2] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 2] + (r2 >> 6)];
                result[Offset + 3] = MobiConst.Vx2MinMaxTable[0x40 + PPixels[Offset + 3] + (r3 >> 6)];
                Offset += 4;//Stride;
                r12--;
                if (r12 <= 0) break;
            }
            return result;
        }
    }
}
