﻿/*
 * Copyright 2019 faddenSoft
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace PluginCommon {
    /// <summary>
    /// Bitmap with 8-bit palette indices, for use with plugin visualizers.
    /// </summary>
    [Serializable]
    public class VisBitmap8 : IVisualization2d {
        public const int MAX_DIMENSION = 4096;

        public int Width { get; private set; }
        public int Height { get; private set; }

        private byte[] mData;
        private int[] mPalette;
        private int mNextColor;

        public VisBitmap8(int width, int height) {
            Debug.Assert(width > 0 && width <= MAX_DIMENSION);
            Debug.Assert(height > 0 && height <= MAX_DIMENSION);

            Width = width;
            Height = height;

            mData = new byte[width * height];
            mPalette = new int[256];
            mNextColor = 0;
        }

        // IVisualization2d
        public int GetPixel(int x, int y) {
            byte pix = mData[x + y * Width];
            return mPalette[pix];
        }

        public void SetPixelIndex(int x, int y, byte colorIndex) {
            if (x < 0 || x >= Width || y < 0 || y >= Width) {
                throw new ArgumentException("Bad x/y: " + x + "," + y + " (width=" + Width +
                    " height=" + Height + ")");
            }
            if (colorIndex < 0 || colorIndex >= mNextColor) {
                throw new ArgumentException("Bad color: " + colorIndex + " (nextCol=" +
                    mNextColor + ")");
            }
            mData[x + y * Width] = colorIndex;
        }

        // IVisualization2d
        public byte[] GetPixels() {
            return mData;
        }

        // IVisualization2d
        public int[] GetPalette() {
            int[] pal = new int[mNextColor];
            for (int i = 0; i < mNextColor; i++) {
                pal[i] = mPalette[i];
            }
            return pal;
        }

        public void AddColor(int color) {
            if (mNextColor == 256) {
                Debug.WriteLine("Palette is full");
                return;
            }
            mPalette[mNextColor++] = color;
        }
    }
}