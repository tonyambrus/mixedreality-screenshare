using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WebRTC
{
    public class MovingAverage
    {
        private float[] window;
        private float total;
        private int numSamples;
        private int insertionIndex;
        private int period;

        public MovingAverage(int period)
        {
            this.period = period;
            window = new float[period];
            Clear();
        }

        public void AddSample(float sample)
        {
            // Advance the insertion index.
            if (this.numSamples != 0)
            {
                this.insertionIndex++;
                if (this.insertionIndex == this.period)
                {
                    this.insertionIndex = 0;
                }
            }

            if (this.numSamples < period)
            {
                this.numSamples++;
            }
            else
            {
                this.total -= this.window[this.insertionIndex];
            }

            this.window[this.insertionIndex] = sample;
            this.total += sample;
        }

        public void Clear()
        {
            this.total = 0;
            this.numSamples = 0;
            this.insertionIndex = 0;
        }

        public bool HasSamples()
        {
            return this.numSamples != 0;
        }

        public float Total
        {
            get { return this.total; }
        }

        public float Average
        {
            get
            {
                return this.numSamples > 0
                    ? (this.total / this.numSamples)
                    : 0;
            }
        }

        public int NumSamples
        {
            get { return this.numSamples; }
        }

        public float LastSample
        {
            get { return numSamples > 0 ? this.window[this.insertionIndex] : 0; }
        }
    };
}
