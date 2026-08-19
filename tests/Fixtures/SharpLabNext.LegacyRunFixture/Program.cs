using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SharpLabNext.LegacyRunFixture
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "throw")
                throw new InvalidOperationException("legacy run fixture failure");
            if (args.Length > 0 && args[0] == "raw")
            {
                using (Stream raw = Console.OpenStandardOutput())
                {
                    byte[] bytes = Encoding.UTF8.GetBytes("SLNR-not-a-frame\n");
                    raw.Write(bytes, 0, bytes.Length);
                }
            }
            Console.Write("stdout:" + string.Join(",", args));
            Console.Error.Write("stderr");
            await Task.Yield();
            return 7;
        }
    }
}
