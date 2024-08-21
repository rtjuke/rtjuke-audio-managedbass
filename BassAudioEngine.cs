using RTJuke.Core.Audio;
using RTJuke.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RTJuke.Core.Plugins.Communication;
using System.Globalization;
using ManagedBass;

namespace RTJuke.Audio.LibManagedBass
{
    public class BassAudioEngine : IAudioEngine
    {
        public string HumanFriendlySettingsText => "";

        public void Start()
        {
            // init BASS
            LogService.Debug("Initializing ManagedBASS AudioEngine");            
            LogService.Info(String.Format("Using BASS version {0}", Bass.Version.ToString()));            

            if (!Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero))
            {                
                LogService.Critical("Failed to init BASS Audio Engine: " + Enum.GetName(typeof(ManagedBass.Errors), Bass.LastError));
                throw new Exception("Failed to init BASS Audio Engine:" + Enum.GetName(typeof(ManagedBass.Errors), Bass.LastError));
            }            
        }


        public void Shutdown()
        {
            Bass.Free();
        }

        public IAudioFile GetAudioFile(string filename)
        {
            return new BassAudioFile(filename);
        }

        public void Init(IMessageBus messageBus)
        {
            // --
        }

        public void SetLocalization(CultureInfo cultureInfo)
        {
            // --
        }

        public bool Configure()
        {
            return true;
        }

        public bool CanBeConfigured()
        {
            return false;
        }

        public string GetSettings()
        {
            return "";
        }

        public bool SetSettings(string settingsStr)
        {
            return true;
        }
    }
}
