using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO;

namespace ConfuserExCustomModuleConstUnpacker
{
    class Program
    {
        static void Main(string[] args)
        {
            var options = args.TakeWhile(s => s.StartsWith("-"));

            if (options.Any(s => s.ToLowerInvariant() == "--unchecked"))
                TargetAssembly.Unchecked = true;

            var infiles = args.Except(options);
            if (!infiles.Any())
            {
                Console.WriteLine("usage: ConfuserExCustomModuleConstUnpacker [--unchecked] <input files..>");
                Console.WriteLine("supply the --unchecked option to skip whitelisting of module call sites. This will permit arbitrary code execution from the target assembly, so use with caution.");
                return;
            }

            try
            {
                foreach (var s in infiles)
                {
                    var absdir = Path.GetDirectoryName(s);
                    if (string.IsNullOrWhiteSpace(absdir))
                        absdir = ".";
                    absdir = Path.GetFullPath(absdir);

                    foreach (var fs in Directory.GetFiles(absdir, Path.GetFileName(s)))
                    {
                        var ta = TargetAssembly.LoadFile(fs);
                        ta.DecryptAndSave(Path.Combine(Path.GetDirectoryName(fs), Path.GetFileNameWithoutExtension(fs) + ".unpacked" + Path.GetExtension(fs)));
                    }
                }
            }
            finally
            {
                if (File.Exists(TargetAssembly.dummy_stub_dll_name))
                    File.Delete(TargetAssembly.dummy_stub_dll_name);
                if (File.Exists(TargetAssembly.module_out_dll_name))
                    File.Delete(TargetAssembly.module_out_dll_name);
            }
        }
    }
}
