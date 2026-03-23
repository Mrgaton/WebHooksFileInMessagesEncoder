using DSFiles_Client.Helpers;
using DSFiles_Shared;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Application = Terminal.Gui.App.Application;
using MessageBox = Terminal.Gui.Views.MessageBox;

namespace DSFiles_Client.CGuis
{
    public partial class Download
    {
        public Download()
        {
            InitializeComponent();

            btnDown.Accepting += async (s, e) =>
            {
                string? fileData = seedTextField.Text.Trim();

                if (fileData.StartsWith("jsp:/", StringComparison.InvariantCultureIgnoreCase)) fileData = Program.GetFromJspaste(fileData); //JSPasteClient.Get(fileData.Split('/').Last()).Result;

                //Console.WriteLine(fileData);

                string[] fileDataSplited = fileData.Split('/')[0].Split(':');

                string[] seedSplited = fileDataSplited.Last().Split('$');

                byte[] seed = seedSplited[0].FromBase64Url();
                byte[]? key = seedSplited.Length > 1 ? seedSplited[1].FromBase64Url() : null;

                string destFileName = string.Empty;

                if (fileDataSplited.Length > 1)
                {
                    destFileName = Encoding.UTF8.GetString(fileDataSplited[0].FromBase64Url().Inflate()) +
                    (
                        fileDataSplited.Length > 2 &&
                        !string.IsNullOrEmpty(fileDataSplited.Skip(1).First()) ? '.' + fileDataSplited.Skip(1).First() : null
                    );
                }

                System.Windows.Forms.SaveFileDialog sfd = new System.Windows.Forms.SaveFileDialog()
                {
                    Title = "Please select where to save the file",
                    FileName = destFileName,
                    ShowHiddenFiles = true,
                    ShowHelp = false
                };

                if (sfd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    MessageBox.ErrorQuery(App!, "DSFiles Manager", "Operation cancelled", "ok", "retry");
                    return;
                }

                var prog = new Progress()
                {
                    ActionToRun = new Action(async () =>
                    {
                        DiscordFilesSpliter.ConsoleProgress = WindowsHelper.GetProgress();

                        string filename = sfd.FileName;

                        Console.WriteLine();

                        using (FileStream fs = File.Open(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                        {
                            await DiscordFilesSpliter.Decode(seed, key, fs);
                        }

                        //byte[] fileSeed = Convert.FromBase64String("AQMgxMbmo5kPC0BEaaeFEhAUUEQAq4USEB4whI2uhRIQCgDEV7GFEhA=");

                        //WFIMEncoder.Decode(fileSeed, "resultado.zip").GetAwaiter().GetResult();

                        //Console.WriteLine(Convert.ToBase64String(WFIMEncoder.Decode(fileSeed).Result).ToString());

                        await Task.Delay(2000);
                    })
                };

                App.Run(prog);
            };
        }
    }
}