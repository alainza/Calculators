using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SerialFiler
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        public string Status
        {
            set
            {
                toolStripStatusLabel1.Text = value;
            }
        }

        private void UpdateState()
        {
            bool isConnected = serialPort1.IsOpen;
            PortCombo.Enabled = !isConnected;
            OpenButton.Enabled = !isConnected;
            CloseButton.Enabled = isConnected;
            sendToolStripMenuItem.Enabled = isConnected;
            receiveToolStripMenuItem.Enabled = isConnected;
        }
        private void OpenButton_Click(object sender, EventArgs e)
        {
            try
            {
                serialPort1.Open();
                Status = serialPort1.PortName + " opened.";
                UpdateState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Unable to open port");
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            PortCombo.SelectedItem = serialPort1.PortName;
            progressBar1.Visible = false;
            UpdateState();
        }

        private void CloseButton_Click(object sender, EventArgs e)
        {
            serialPort1.Close();
            Status = serialPort1.PortName + " opened.";
            UpdateState();
        }

        private void PortCombo_SelectedValueChanged(object sender, EventArgs e)
        {
            if (!serialPort1.IsOpen)
            {
                serialPort1.PortName = PortCombo.SelectedItem as string;
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void sendToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                serialPort1.Encoding = Encoding.ASCII;
                if (openFileDialog1.ShowDialog(this) == DialogResult.OK)
                {
                    byte[] content;
                    if (TrimLinesNumCheckbox.Checked)
                    {
                        int firstLine = 10;
                        int increment = 10;
                        if (OneIncrementRB.Checked)
                        {
                            firstLine = 1;
                            increment = 1;
                        }
                        String[] lines = System.IO.File.ReadAllLines(openFileDialog1.FileName);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            lines[i] = firstLine.ToString() + " " + lines[i];
                            firstLine += increment;
                        }
                        content = Encoding.ASCII.GetBytes(String.Join("\r\n", lines));
                    }
                    else
                    {
                        content = System.IO.File.ReadAllBytes(openFileDialog1.FileName);
                    }
                    progressBar1.Minimum = 0;
                    progressBar1.Maximum = content.Length;
                    progressBar1.Value = 0;
                    progressBar1.Visible = true;
                    int lineDelay = (int)LineDelayUpDown.Value;
                    int sent = 0;
                    while (sent  < content.Length)
                    {
                        // Send up to the end of the current line (at most 80 bytes at a time)
                        int toSend = 0;
                        bool endOfLine = false;
                        while (sent + toSend < content.Length && toSend < 80)
                        {
                            byte b = content[sent + toSend];
                            toSend++;
                            if (b == (byte)'\n')
                            {
                                endOfLine = true;
                                break;
                            }
                        }
                        serialPort1.Write(content, sent, toSend);
                        sent += toSend;
                        progressBar1.Value = sent;
                        Application.DoEvents();
                        if (endOfLine && lineDelay > 0)
                        {
                            // Give the receiver time to process the line
                            PauseAfterLine(lineDelay);
                        }
                    }
                    byte[] buffer = new byte[1];
                    buffer[0] = 26;
                    serialPort1.Write(buffer, 0, 1);
                    MessageBox.Show("Sent !");
                    progressBar1.Visible = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Unable to open port");
            }
        }

        /// <summary>
        /// Waits until everything written so far has left the PC's output buffer,
        /// then waits <paramref name="milliseconds"/> more, keeping the window responsive.
        /// </summary>
        private void PauseAfterLine(int milliseconds)
        {
            Stopwatch watch = Stopwatch.StartNew();
            // Do not wait forever if the receiver holds the line (flow control): 30 s at most
            while (serialPort1.BytesToWrite > 0 && watch.ElapsedMilliseconds < 30000)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            watch.Restart();
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
        }

        private void receiveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                serialPort1.Encoding = Encoding.ASCII;
                serialPort1.ReadTimeout = -1;
                if (saveFileDialog1.ShowDialog(this) == DialogResult.OK)
                {
                    MessageBox.Show("Send the file from the Calculator");
                    serialPort1.ReceivedBytesThreshold = 1;
                    serialPort1.Handshake = System.IO.Ports.Handshake.RequestToSendXOnXOff;
                    byte[] buffer = new byte[4096];
                    System.IO.MemoryStream ms = new System.IO.MemoryStream();

                    int totalRead = 0;
                    int totalExpected = 1024;
                    progressBar1.Minimum = 0;
                    progressBar1.Maximum = totalExpected;
                    progressBar1.Value = 0;
                    progressBar1.Visible = true;

                    bool endOfFile = false;
                    while (!endOfFile)
                    {
                        int received = serialPort1.Read(buffer, 0, buffer.Length);
                        for (int i = 0; i < received; i++)
                        {
                            if (buffer[i] == 26)
                            {
                                received = i;
                                endOfFile = true;
                                break;
                            }
                        }

                        ms.Write(buffer, 0, received);
                        totalRead += received;
                        if (totalRead >= totalExpected)
                        {
                            totalExpected = (int)(totalRead * 1.2);
                            progressBar1.Maximum = totalExpected;
                        }
                        progressBar1.Value = totalRead;
                        Application.DoEvents();
                    }
                    ms.Position = 0;
                    String text = System.Text.Encoding.ASCII.GetString(ms.ToArray());
                    if (TrimLinesNumCheckbox.Checked)
                    {
                        String[] lines = text.Split("\n\r".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                        {
                            String line = lines[lineIndex];
                            int idx = 0;
                            while (idx < line.Length)
                            {
                                char c = line[idx];
                                if (c == ' ')
                                {
                                    // Skip only the first space
                                    idx++;
                                    break;
                                }
                                else if ("0123456789".IndexOf(c) == -1)
                                {
                                    // Do not skip any other character
                                    break;
                                }
                                idx++;
                            }
                            if (idx == line.Length)
                            {
                                lines[lineIndex] = String.Empty;
                            } else {
                                lines[lineIndex] = line.Substring(idx);
                            }
                        }
                        System.IO.File.WriteAllLines(saveFileDialog1.FileName, lines);
                    } else
                    {
                        System.IO.File.WriteAllText(saveFileDialog1.FileName, text);
                    }
                    MessageBox.Show($"{totalRead} bytes received.");
                    progressBar1.Visible = false;

                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Unable to open port");
            }
        }

        private void SerialPort1_DataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
        {
            throw new NotImplementedException();
        }

    }
}
