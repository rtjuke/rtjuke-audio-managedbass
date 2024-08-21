using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RTJuke.Audio.LibManagedBass.Exceptions
{
    public class BassException : Exception
    {

        public BassException(ManagedBass.Errors bass_errorcode) : base(bass_errorcode.ToString())
        {
            
        }

    }
}
