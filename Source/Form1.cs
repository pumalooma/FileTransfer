using Lidgren.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace FileTransfer {
    public partial class Form1 : Form {

        private NetPeer peer;
        private Stopwatch stopWatch = new Stopwatch();
        private bool foundServer = false;
        private int mPort = 12347;
        double bytesToMb = 1.0 / 1024.0 / 1024.0;
        long bucketSize = 1024 * 1024;
		private string connectToIp;

        private enum MessageType { eCompleteFile, ePartialHeader, ePartialSegment, ePartialFooter, eReadyForMore };

        private List<string> mFileQueue = new List<string>();
        private FileStream mFileStreamIn;
        private FileStream mFileStreamOut;
        private bool mReadyToSendMore = true;

        public Form1 () {
            InitializeComponent();

			var args = Environment.GetCommandLineArgs();
			if(args.Length == 2) {
				connectToIp = args[1];
            }

            InitNetwork();
                        
            var timer = new Timer();
            timer.Interval = 10;
            timer.Tick += new EventHandler(TimerUpdate);
            timer.Start();

            AllowDrop = true;
            DragEnter += new DragEventHandler(Form1_DragEnter);
            DragDrop += new DragEventHandler(Form1_DragDrop);
        }

        private void InitNetwork() {

            var config = new NetPeerConfiguration("file transfer");
			if (connectToIp == null) {
				LogText("Searching for server...");
				config.EnableMessageType(NetIncomingMessageType.DiscoveryResponse);
				stopWatch.Start();
			}
			var client = new NetClient(config);
            client.Start();

			if(connectToIp == null)
				client.DiscoverLocalPeers(mPort);
			else {
				LogText("Connecting to server:" + connectToIp);
				client.Connect(connectToIp, mPort);
				foundServer = true;
            }
			peer = client;
        }

        private void TimerUpdate (object sender, EventArgs e) {
            NetIncomingMessage msg;
            while ((msg = peer.ReadMessage()) != null) {
                switch (msg.MessageType) {
                    case NetIncomingMessageType.Data:
                        HandleIncomingMessage(msg);
                        break;
                    case NetIncomingMessageType.DiscoveryResponse:
                        LogText("Connected to server: " + msg.SenderEndPoint);
                        peer.Connect(msg.SenderEndPoint.Address.ToString(), mPort);
                        foundServer = true;
                        break;
                    case NetIncomingMessageType.DiscoveryRequest:
                        LogText("Client connected.");
                        NetOutgoingMessage response = peer.CreateMessage();
                        peer.SendDiscoveryResponse(response, msg.SenderEndPoint);
                        break;
                    case NetIncomingMessageType.VerboseDebugMessage:
                    case NetIncomingMessageType.DebugMessage:
                    case NetIncomingMessageType.WarningMessage:
                    case NetIncomingMessageType.ErrorMessage:
                        LogText(msg.ReadString());
						break;
					case NetIncomingMessageType.StatusChanged:
						msg.ReadByte();
						string reason = msg.ReadString();
						LogText(reason);
						break;
					default:
						LogText(msg.MessageType.ToString());
						break;
				}
            }

            if (mReadyToSendMore) {
                if(mFileStreamIn != null ) {
                    SendSegment();
                }
                else if (mFileQueue.Count > 0) {
                    SendFile(mFileQueue[0]);
                    mFileQueue.RemoveAt(0);
                }
            }
			
			if (!foundServer && stopWatch.Elapsed > TimeSpan.FromSeconds(3.0)) {
                foundServer = true;
				stopWatch.Stop();

                LogText("No server found, creating one.");

                var config = new NetPeerConfiguration("file transfer");
                config = new NetPeerConfiguration("file transfer") { Port = mPort };
                config.EnableMessageType(NetIncomingMessageType.DiscoveryRequest);
                var server = new NetServer(config);
                server.Start();
                peer = server;

                LogText("Server created.");
            }
        }

        private void HandleIncomingMessage(NetIncomingMessage msg) {
            MessageType messageType = (MessageType)msg.ReadByte();
            if(messageType == MessageType.eReadyForMore) {
                bool endOfFile = msg.ReadBoolean();
                if(endOfFile)
                    LogText("Other peer signaled that it received the file.");
                mReadyToSendMore = true;
            }
            else if(messageType == MessageType.eCompleteFile) {
                string fileName = msg.ReadString();
                int length = msg.ReadInt32();
                byte[] bytes = msg.ReadBytes(length);
                File.WriteAllBytes(fileName, bytes);

                progressBar.Maximum = 1;
                progressBar.Value = 1;

                LogText(string.Format("Received file: {0} ({1:F1}mb)", fileName, length * bytesToMb));

                SignalReadyForMore(true);
            }
            else if(messageType == MessageType.ePartialHeader) {
                string fileName = msg.ReadString();
                long length = msg.ReadInt64();
                int buckets = msg.ReadInt32();

                LogText(string.Format("Receiving large file: {0} ({1:F1}mb) ({2} segments)", fileName, length * bytesToMb, buckets));

                mFileStreamOut = File.OpenWrite(fileName);

                progressBar.Maximum = buckets;
                progressBar.Value = 0;

                SignalReadyForMore(false);
            }
            else if(messageType == MessageType.ePartialSegment) {
                int count = msg.ReadInt32();
                byte[] bytes = msg.ReadBytes(count);
                mFileStreamOut.Write(bytes, 0, count);

                progressBar.Value++;

                LogText("received content segment: (" + count + " bytes)");
                SignalReadyForMore(false);
            }
            else if (messageType == MessageType.ePartialFooter) {
                int count = msg.ReadInt32();
                byte[] bytes = msg.ReadBytes(count);
                mFileStreamOut.Write(bytes, 0, count);
                mFileStreamOut.Close();
                mFileStreamOut = null;

                LogText("received final segment: (" + count + " bytes)");
                SignalReadyForMore(true);
            }
        }

        private void SignalReadyForMore(bool endOfFile) {
            NetOutgoingMessage sendMsg = peer.CreateMessage();
            sendMsg.Write((byte)MessageType.eReadyForMore);
            sendMsg.Write(endOfFile);
            peer.SendMessage(sendMsg, peer.Connections, NetDeliveryMethod.ReliableOrdered, 0);
        }


        private void SendFile (string filePath) {
            if (peer == null || peer.Connections == null || peer.Connections.Count == 0) {
                LogText("Invalid peer, could not send file.");
                return;
            }

            mReadyToSendMore = false;

            long fileSize = new FileInfo(filePath).Length;
            if( fileSize <= bucketSize ) {
                byte[] bytes = File.ReadAllBytes(filePath);

                LogText(string.Format("Sending file: {0} ({1:F1}mb)", Path.GetFileName(filePath), bytes.Length * bytesToMb));

                NetOutgoingMessage sendMsg = peer.CreateMessage();

                sendMsg.Write((byte)MessageType.eCompleteFile);
                sendMsg.Write(Path.GetFileName(filePath));
                sendMsg.Write(bytes.Length);
                sendMsg.Write(bytes);

                peer.SendMessage(sendMsg, peer.Connections, NetDeliveryMethod.ReliableOrdered, 0);
                
                progressBar.Maximum = 1;
                progressBar.Value = 1;
            }
            else {
                mFileStreamIn = File.OpenRead(filePath);

                int bucketCount = (int)(fileSize / bucketSize);
                LogText(string.Format("Sending large file in {0} segments: {1} ({2:F1}mb)", bucketCount, Path.GetFileName(filePath), fileSize * bytesToMb));

                NetOutgoingMessage sendMsg = peer.CreateMessage();

                sendMsg.Write((byte)MessageType.ePartialHeader);
                sendMsg.Write(Path.GetFileName(filePath));
                sendMsg.Write(fileSize);
                sendMsg.Write(bucketCount);

                peer.SendMessage(sendMsg, peer.Connections, NetDeliveryMethod.ReliableOrdered, 0);
                
                progressBar.Maximum = bucketCount;
                progressBar.Value = 0;
            }

        }

        private void SendSegment() {
            
            byte[] bytes = new byte[bucketSize];
            int length = mFileStreamIn.Read(bytes, 0, (int)bucketSize);

            NetOutgoingMessage sendMsg = peer.CreateMessage();
            
            if(length == bucketSize)
                sendMsg.Write((byte)MessageType.ePartialSegment);
            else
                sendMsg.Write((byte)MessageType.ePartialFooter);
            sendMsg.Write(length);
            sendMsg.Write(bytes, 0, length);

            if(length < bucketSize) {
                mFileStreamIn.Close();
                mFileStreamIn = null;
            }
            else
                progressBar.Value++;

            LogText(string.Format("Sending {0} segment: ({1} bytes)", length == bucketSize ? "content" : "final", length));

            peer.SendMessage(sendMsg, peer.Connections, NetDeliveryMethod.ReliableOrdered, 0);
        }

        private void LogText(string text) {
            txtInfo.AppendText(text + "\r\n");
        }
        
        void Form1_DragEnter (object sender, DragEventArgs e) {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }
        void Form1_DragDrop (object sender, DragEventArgs e) {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string filePath in files)
                mFileQueue.Add(filePath);
        }
    }
}
