using RTJuke.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RTJuke.Audio.LibManagedBass
{
    public class BassPluginLibrary : IPluginLibrary
    {
        public IEnumerable<PluginInfo> GetContainedPlugins()
        {
            yield return new PluginInfo(PluginType.AudioEngine, "BASS_AUDIO_ENGINE", "Managed Bass Audio Engine", typeof(BassAudioEngine), new Version(1, 0), false, "");
        }
    }
}
