using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RTJuke.Core.Audio.Normalization;
using RTJuke.Core.Audio;
using System.Threading.Tasks;
using System.Threading;
using RTJuke.Core.Logging;
using System.IO;
using ManagedBass;
using System.Timers;

namespace RTJuke.Audio.LibManagedBass
{    
    /// <summary>
    /// Kapselt einen BASS-Stream
    /// </summary>
    public class BassAudioFile : IAudioFile
    {
        protected System.Timers.Timer timer;

        protected SyncProcedure _mySync;
        protected DSPProcedure _myDSP;
        protected DownloadProcedure _myDownload;

        protected bool isClosed = false;

        protected int bass_stream = 0;
        protected int streamEndSync = 0; // handle for stream end event
        protected int streamDspSync = 0; // handle for dsp sync
        protected long currentpos_bytes = -1;
        protected double currentpos_secs = -1;

        protected int _volume = 255;

        protected TimeSpan? _length = null;

        public String Filename { get; private set; }

        protected SemaphoreSlim loadSemaphore = new SemaphoreSlim(1, 1);

        protected PlayState currentState = PlayState.Unknown;

        public PlayState CurrentState
        {
            get { return currentState; }
            protected set
            {
                if (currentState != value)
                {
                    currentState = value;
                    OnPlayStateChanged();                    
                }
            }
        }

        /// <summary>
        /// Die Lautstärke 0 - 255
        /// </summary>
        public int Volume
        {
            get
            {                
                return _volume;
            }
            set
            {
                if (value >= 0 && value <= 255)
                {
                    _volume = value;
                    Bass.ChannelSetAttribute(bass_stream, ChannelAttribute.Volume, value / 255.0f);
                }
            }
        }

        protected TimeSpan? storedPosition;

        public TimeSpan Position
        {            
            get
            {
                if (storedPosition.HasValue)                
                    return storedPosition.Value;
                
                currentpos_bytes = Bass.ChannelGetPosition(bass_stream);
                currentpos_secs = Bass.ChannelBytes2Seconds(bass_stream, currentpos_bytes);          
                
                return TimeSpan.FromSeconds(currentpos_secs);                
            }
            set
            {
                if (bass_stream != 0)
                {
                    long bytes = Bass.ChannelSeconds2Bytes(bass_stream, value.TotalSeconds);
                    Bass.ChannelSetPosition(bass_stream, bytes, PositionFlags.Bytes);

                    // check if the position was set correctly
                    if (Bass.ChannelGetPosition(bass_stream) < bytes)
                        // unable to set the desired position
                        // since the buffer was not filled enough
                        storedPosition = value;
                    else
                        storedPosition = null;                    
                }
                else
                {
                    // the stream has not yet been loaded, store the position to set it when the stream has been opened
                    storedPosition = value;
                }

                OnPositionChanged();
            }
        }

        public TimeSpan? Length
        {
            get
            {
                return _length;
            }
            protected set
            {
                if (_length != value)
                {
                    _length = value;
                    OnLengthChanged();
                }
            }
        }

        
        public bool IsBuffering
        {
            get => bufferProgress < 1.0 || storedPosition.HasValue;
        }

        double bufferProgress = 0;
        public double BufferProgress
        {
            get => IsBuffering ? bufferProgress : 1.0;
            protected set
            {
                if (!double.Equals(bufferProgress, value))
                {
                    bufferProgress = value;
                    OnBufferProgressChanged();
                }
            }
        }

        /// <summary>
        /// Load the stream async
        /// </summary>        
        protected virtual async Task<bool> CreateStreamAsync(string filename)
        {
            CurrentState = PlayState.Opening;

            await Task.Run(() => {

                //if (new Uri(filename, UriKind.RelativeOrAbsolute).IsFile)
                if (true) // TODO: fix exception: IsFIle not supported for relative URI
                {
                    bass_stream = Bass.CreateStream(filename, 0, 0, BassFlags.Default | BassFlags.Byte);

                    if (bass_stream == 0)
                    {
                        var err = Bass.LastError;
                        LogService.Error("Error while creating bass stream: " + err.ToString());
                    }
                    else
                    {                        
                        BufferProgress = 1.0;
                    }
                }
                else
                {
                    // filename seems to be a stream URI
                    // todo: register download proc to show buffer progress
                    // use the overload of CreateStream which uses BASS_StreamCreateUrl internally
                    _myDownload = new DownloadProcedure(DownloadProc);                    
                    bass_stream = Bass.CreateStream(filename, 0, BassFlags.Default | BassFlags.Byte, _myDownload);
                    
                    if (bass_stream == 0)
                    {
                        var err = Bass.LastError;
                        LogService.Error("Error while creating bass stream: " + err.ToString());
                    } else
                        BufferProgress = 0;
                }
            }).ConfigureAwait(false);

            return bass_stream != 0;

        }

        public BassAudioFile(String filename)
        {
            Filename = filename;                                   
        }

        public virtual async Task<bool> LoadAsync()
        {           
            try
            {
                await loadSemaphore.WaitAsync();

                if (bass_stream != 0) // already loaded?
                    return true;

                if (await CreateStreamAsync(Filename))
                {
                    // retrieve the stream length
                    long p = Bass.ChannelGetLength(bass_stream);
                    double secs = Bass.ChannelBytes2Seconds(bass_stream, p);
                    Length = TimeSpan.FromSeconds(secs);

                    CurrentState = PlayState.ReadyToPlay;

                    timer = new System.Timers.Timer(500);                       
                    timer.Elapsed += timer_Tick;
                    timer.Enabled = true;                    

                     // register events     
                     _mySync = new SyncProcedure(EndSync);
                    streamEndSync = Bass.ChannelSetSync(bass_stream, SyncFlags.End | SyncFlags.Mixtime, 0, _mySync, IntPtr.Zero);                    

                    return true;
                }
                else
                {
                    CurrentState = PlayState.Error;
                    return false;
                }
            }
            finally
            {
                loadSemaphore.Release();
            }
        }

        public void SetNormalizer(INormalizer normalizer, NormalizationInfo normalizationInfo)
        {
            _myDSP = new DSPProcedure(NormalizeDSP);
            streamDspSync = Bass.ChannelSetDSP(bass_stream, _myDSP, IntPtr.Zero, 42);
        }

        private void NormalizeDSP(int handle, int channel, IntPtr buffer, int length, IntPtr peak)
        {

        }

        protected void EndSync(int handle, int channel, int data, IntPtr user)
        {
            // Der Stream hat das Ende erreicht
            CurrentState = PlayState.Ended;           
        }

        private void DownloadProc(IntPtr Buffer, int Length, IntPtr User)
        {
            // buffering
            if (IsBuffering)
            {
                long bufferedLen = Bass.StreamGetFilePosition(bass_stream, FileStreamPosition.Buffer);

                if (bufferedLen == -1)
                {
                    BufferProgress = 0;
                }
                else
                {
                    long len = Bass.StreamGetFilePosition(bass_stream, FileStreamPosition.End);

                    // we want to set a position which is not yet in the buffer can we do it now?
                    if (storedPosition.HasValue && this.Length.HasValue)
                    {
                        double targetBufferPercentage = (storedPosition?.TotalSeconds ?? 0) / this.Length.Value.TotalSeconds;

                        if (BufferProgress >= targetBufferPercentage)
                        {
                            Position = storedPosition.Value;

                            // start playing if the user wanted to
                            if (CurrentState == PlayState.Playing)
                            {
                                FadeIn(2000);
                                Play();
                            }                                
                        }
                    }

                    if (len > 0)
                        BufferProgress = bufferedLen / (double)len;                                        
                }
            }
        }

        private void timer_Tick(object sender, EventArgs e)        
        {
            long p = Bass.ChannelGetPosition(bass_stream);
            if (p != currentpos_bytes)
            {
                currentpos_bytes = p;
                currentpos_secs = Bass.ChannelBytes2Seconds(bass_stream, currentpos_bytes);
                OnPositionChanged();                
            }            
        }        

        /// <summary>
        /// Die Tags des Streams lesen (bspw. ID3v2, ID3v1, APETags, WMATags)
        /// </summary>
        /// <returns></returns>
        public async Task<AudioTags> ReadTags()
        {
            return await Task.Run<AudioTags>(async () =>
            {
                if (bass_stream == 0)
                {
                    if (!(await LoadAsync()))
                        return null;
                }

                var bassTags = TagReader.Read(bass_stream);
                if (bassTags != null)
                {
                    AudioTags tags = new AudioTags();
                    tags.Track = bassTags.Track;
                    tags.Title = bassTags.Title;
                    tags.Album = bassTags.Album;
                    tags.Artist = bassTags.Artist;

                    if (bassTags.Genre != null)
                    {
                        foreach (var g in bassTags.Genre.Split(';'))
                            tags.Genres.Add(g);
                    }

                    return tags;
                }

                return null;
            });            
        }

        public virtual void Play()
        {
            if (storedPosition.HasValue)
            {
                // we cannot start playing since the buffer did not reach the position yet
                // but we will start as soon as we have loaded the desired position
                CurrentState = PlayState.Playing;
                return;
            }

            if (Bass.ChannelPlay(bass_stream, false))            
                CurrentState = PlayState.Playing;
            else
                CurrentState = PlayState.Error;
        }

        public void Pause()
        {
            if (Bass.ChannelPause(bass_stream))
                CurrentState = PlayState.Paused;
            else
                CurrentState = PlayState.Error;
        }

        public void Stop()
        {
            if (Bass.ChannelStop(bass_stream))
                CurrentState = PlayState.Stopped;
            else
                CurrentState = PlayState.Error;
        }

        public void Close()
        {
            isClosed = true;
            
            if (timer != null)
                timer.Dispose();

            if (bass_stream != -1)
            {
                if (streamEndSync != 0)
                    Bass.ChannelRemoveSync(bass_stream, streamEndSync);
                if (streamDspSync != 0)
                    Bass.ChannelRemoveDSP(bass_stream, streamDspSync);
                Bass.StreamFree(bass_stream);
            }

            CurrentState = PlayState.Closed;
        }

        public void SetKnownLength(TimeSpan tSpan)
        {
            Length = tSpan;
        }

        public void Dispose()
        {
            if (!isClosed)            
                Close();            
        }

        public void FadeIn(int milliseconds)
        {            
            Bass.ChannelSetAttribute(bass_stream, ChannelAttribute.Volume, 0);
            Bass.ChannelSlideAttribute(bass_stream, ChannelAttribute.Volume, Volume / 255.0f, milliseconds);
        }

        public void FadeOut(int milliseconds)
        {
            Bass.ChannelSetAttribute(bass_stream, ChannelAttribute.Volume, Volume / 255.0f);
            Bass.ChannelSlideAttribute(bass_stream, ChannelAttribute.Volume, 0, milliseconds);
        }

        /// <summary>
        /// Returns the bass channel on which this audio file is played
        /// </summary>        
        [Obsolete]
        public int GetBassChannel()
        {
            return bass_stream;
        }        

        #region Ereignisse

        public event EventHandler PositionChanged;

        protected void OnPositionChanged()
        {
            PositionChanged?.Invoke(this, EventArgs.Empty);            
        }

        public event EventHandler PlayStateChanged;

        protected void OnPlayStateChanged()
        {
            var eh = PlayStateChanged;
            if (eh != null)
                eh(this, EventArgs.Empty);
        }

        public event EventHandler LengthChanged;        

        protected void OnLengthChanged()
        {
            var eh = LengthChanged;
            if (eh != null)
                eh(this, EventArgs.Empty);
        }

        public event EventHandler BufferStateChanged;

        protected void OnBufferProgressChanged()
        {
            var eh = BufferStateChanged;
            if (eh != null)
                eh(this, EventArgs.Empty);
        }

        #endregion

        public bool SupportsFft(out int numBands)
        {
            numBands = 2048;
            return true;
        }

        public int GetSampleRate()
        {
            float sr;
            Bass.ChannelGetAttribute(bass_stream, ChannelAttribute.Frequency, out sr);
            return (int)sr;
        }

        public bool GetFftData(float[] fftResultBuffer)
        {
            if (fftResultBuffer.Length != 2048)
                return false;
            
            if (bass_stream != -1)
                return Bass.ChannelGetData(bass_stream, fftResultBuffer, (int)ManagedBass.DataFlags.FFT2048) != -1;
            else
                return false;
        }
    }
}
