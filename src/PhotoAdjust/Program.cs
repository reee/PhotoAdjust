namespace PhotoAdjust;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // 命令行模式（用于自动化测试）: --process 输出.jpg 输入.jpg
        if (args.Length == 3 && args[0] == "--process")
        {
            try
            {
                var result = PhotoProcessor.Process(args[2]);
                File.WriteAllBytes(args[1], result.Data);
                Console.WriteLine($"OK {result.FileSize} bytes -> {args[1]}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
        return 0;
    }
}
