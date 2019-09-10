using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace StackExchange.Redis
{
    public partial class ConnectionMultiplexer
    {
        [Conditional("VERBOSE")]
        partial void OnTrace(string message, string category) => Trace.WriteLine(message, category);
    }
}
