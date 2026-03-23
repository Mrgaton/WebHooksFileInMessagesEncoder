using System;
using System.Threading;

namespace DSFiles_Client.CGuis
{
    public partial class Progress
    {
        public Action ActionToRun { get; set; }

        public Progress()
        {
            InitializeComponent();
         
            Thread staThread = new Thread(() => ActionToRun());
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();

            //Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }
    }
}