﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using aclogview.Properties;

namespace aclogview
{
    public partial class FindOpcodeInFilesForm : Form
    {
        public FindOpcodeInFilesForm()
        {
            InitializeComponent();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            txtSearchPathRoot.Text = Settings.Default.FindOpcodeInFilesRoot;
            txtOpcode.Text = Settings.Default.FindOpcodeInFilesOpcode.ToString("X4");

            typeof(DataGridView).InvokeMember("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty, null, dataGridView1, new object[] { true });
            dataGridView1.RowHeadersVisible = false;
            dataGridView1.AllowUserToAddRows = false;
            dataGridView1.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dataGridView1.Columns[0].ValueType = typeof(int);
            dataGridView1.Columns[1].ValueType = typeof(int);

            // Center to our owner, if we have one
            if (Owner != null)
                Location = new Point(Owner.Location.X + Owner.Width / 2 - Width / 2, Owner.Location.Y + Owner.Height / 2 - Height / 2);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            searchAborted = true;

            Settings.Default.FindOpcodeInFilesRoot = txtSearchPathRoot.Text;
            Settings.Default.FindOpcodeInFilesOpcode = OpCode;

            base.OnClosing(e);
        }

        int OpCode
        {
            get
            {
                int value;

                int.TryParse(txtOpcode.Text, NumberStyles.HexNumber, null, out value);

                return value;
            }
        }

        private void btnChangeSearchPathRoot_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog openFolder = new FolderBrowserDialog())
            {
                if (openFolder.ShowDialog() == DialogResult.OK)
                    txtSearchPathRoot.Text = openFolder.SelectedPath;
            }
        }


        private readonly List<string> filesToProcess = new List<string>();
        private int opCodeToSearchFor;
        private int filesProcessed;
        private bool searchAborted;

        private class ProcessFileResut
        {
            public int Hits;
            public string FileName;
        }

        private readonly ConcurrentBag<ProcessFileResut> processFileResuts = new ConcurrentBag<ProcessFileResut>();
        
        private readonly ConcurrentDictionary<string, int> specialOutputHits = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentQueue<string> specialOutputHitsQueue = new ConcurrentQueue<string>();

        private void btnStartSearch_Click(object sender, EventArgs e)
        {
            dataGridView1.RowCount = 0;

            try
            {
                btnStartSearch.Enabled = false;

                filesToProcess.Clear();
                opCodeToSearchFor = OpCode;
                filesProcessed = 0;
                searchAborted = false;

                ProcessFileResut result;
                while (!processFileResuts.IsEmpty)
                    processFileResuts.TryTake(out result);


                specialOutputHits.Clear();
                string specialOutputHitsResult;
                while (!specialOutputHitsQueue.IsEmpty)
                    specialOutputHitsQueue.TryDequeue(out specialOutputHitsResult);
                richTextBox1.Clear();


                filesToProcess.AddRange(Directory.GetFiles(txtSearchPathRoot.Text, "*.pcap", SearchOption.AllDirectories));
                filesToProcess.AddRange(Directory.GetFiles(txtSearchPathRoot.Text, "*.pcapng", SearchOption.AllDirectories));

                toolStripStatusLabel1.Text = "Files Processed: 0 of " + filesToProcess.Count;

                txtSearchPathRoot.Enabled = false;
                txtOpcode.Enabled = false;
                btnChangeSearchPathRoot.Enabled = false;
                btnStopSearch.Enabled = true;

                timer1.Start();

                new Thread(() =>
                {
                    // Do the actual search here
                    DoSearch();

                    if (!Disposing && !IsDisposed)
                        btnStopSearch.BeginInvoke((Action)(() => btnStopSearch_Click(null, null)));
                }).Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString());

                btnStopSearch_Click(null, null);
            }
        }

        private void btnStopSearch_Click(object sender, EventArgs e)
        {
            searchAborted = true;

            timer1.Stop();

            timer1_Tick(null, null);

            txtSearchPathRoot.Enabled = true;
            txtOpcode.Enabled = true;
            btnChangeSearchPathRoot.Enabled = true;
            btnStartSearch.Enabled = true;
            btnStopSearch.Enabled = false;
        }


        private void DoSearch()
        {
            Parallel.ForEach(filesToProcess, (currentFile) =>
            {
                if (searchAborted || Disposing || IsDisposed)
                    return;

                try
                {
                    ProcessFile(currentFile);
                }
                catch { }
            });
        }

        private void ProcessFile(string fileName)
        {
            int hits = 0;

            var records = PCapReader.LoadPcap(fileName, ref searchAborted);

            // We could put the abort check in the foreach, but the only downside to having it here is if you abort/close during a huge log parse, you have to wait a few seconds for the log to finish
            // before the app actually terminates.
            if (searchAborted || Disposing || IsDisposed)
                return;

            foreach (var record in records)
            {
                if (record.opcodes.Contains((PacketOpcode)opCodeToSearchFor))
                    hits++;

                // Custom search code that can output information to Special Output
                foreach (BlobFrag frag in record.netPacket.fragList_)
                {
                    if (frag.dat_.Length <= 20) // ITS IMPORTANT THAT YOU MAKE SURE YOU HAVE THE CORRECT LENGTH HERE. If your target is shorter than this, it will be skipped
                        continue;

                    BinaryReader fragDataReader = new BinaryReader(new MemoryStream(frag.dat_));

                    var messageCode = fragDataReader.ReadUInt32();

                    /*if (messageCode == 0x02BB) // Creature Message
                    {
                        var parsed = CM_Communication.HearSpeech.read(fragDataReader);

                        //if (parsed.ChatMessageType != 0x0C)
                        //    continue;

                        var output = parsed.ChatMessageType.ToString("X4") + " " + parsed.MessageText;

                        if (!specialOutputHits.ContainsKey(output))
                        {
                            if (specialOutputHits.TryAdd(output, 0))
                                specialOutputHitsQueue.Enqueue(output);
                        }
                    }*/

                    /*if (messageCode == 0xF7B0) // Game Event
                    {
                        var character = fragDataReader.ReadUInt32(); // Character
                        var sequence = fragDataReader.ReadUInt32(); // Sequence
                        var _event = fragDataReader.ReadUInt32(); // Event

                        if (_event == 0x0147) // Group Chat
                        {
                            var parsed = CM_Communication.ChannelBroadcast.read(fragDataReader);

                            var output = parsed.GroupChatType.ToString("X4");
                            if (!specialOutputHits.ContainsKey(output))
                            {
                                if (specialOutputHits.TryAdd(output, 0))
                                    specialOutputHitsQueue.Enqueue(output);
                            }
                        }

                        if (_event == 0x02BD) // Tell
                        {
                            var parsed = CM_Communication.HearDirectSpeech.read(fragDataReader);

                            var output = parsed.ChatMessageType.ToString("X4");

                            if (!specialOutputHits.ContainsKey(output))
                            {
                                if (specialOutputHits.TryAdd(output, 0))
                                    specialOutputHitsQueue.Enqueue(output);
                            }
                        }
                    }*/

                    /*if (messageCode == 0xF7B1) // Game Action
                    {
                    }*/

                    /*if (messageCode == 0xF7DE) // TurbineChat
                    {
                        var parsed = CM_Admin.ChatServerData.read(fragDataReader);

                        string output = parsed.TurbineChatType.ToString("X2");

                        if (!specialOutputHits.ContainsKey(output))
                        {
                            if (specialOutputHits.TryAdd(output, 0))
                                specialOutputHitsQueue.Enqueue(output);
                        }
                    }*/

                    /*if (messageCode == 0xF7E0) // Server Message
                    {
                        var parsed = CM_Communication.TextBoxString.read(fragDataReader);

                        //var output = parsed.ChatMessageType.ToString("X4") + " " + parsed.MessageText + ",";
                        var output = parsed.ChatMessageType.ToString("X4");

                        if (!specialOutputHits.ContainsKey(output))
                        {
                            if (specialOutputHits.TryAdd(output, 0))
                                specialOutputHitsQueue.Enqueue(output);
                        }
                    }*/
                }
            }

            Interlocked.Increment(ref filesProcessed);

            processFileResuts.Add(new ProcessFileResut() { Hits = hits, FileName = fileName });
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            ProcessFileResut result;
            while (!processFileResuts.IsEmpty)
            {
                if (processFileResuts.TryTake(out result))
                {
                    var length = new FileInfo(result.FileName).Length;

                    if (result.Hits > 0)
                        dataGridView1.Rows.Add(result.Hits, length, result.FileName);
                }
            }

            string specialOutputHitsQueueResult;
            while (!specialOutputHitsQueue.IsEmpty)
            {
                if (specialOutputHitsQueue.TryDequeue(out specialOutputHitsQueueResult))
                    richTextBox1.Text += specialOutputHitsQueueResult + Environment.NewLine;
            }

            toolStripStatusLabel1.Text = "Files Processed: " + filesProcessed + " of " + filesToProcess.Count;
        }


        private void dataGridView1_CellContentDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex == -1)
                return;

            var fileName = (string)dataGridView1.Rows[e.RowIndex].Cells[2].Value;

            System.Diagnostics.Process.Start(Application.ExecutablePath, '"' + fileName + '"' + " " + opCodeToSearchFor);
        }
    }
}