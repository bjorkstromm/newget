using System;

namespace NewGet.Sample
{
    class Program
    {
        static void Main(string[] args)
        {
            var installer = new NewGetPackageInstaller();

            var files = installer.InstallPackageAsync("LitJson", "0.11.0").Result;

            Console.WriteLine(string.Join("\n", files));
        }
    }
}