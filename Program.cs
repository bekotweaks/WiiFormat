using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace WiiFormat
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }

    // ---------- FAT32 formatting engine ----------
    static class Fat32
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share,
            IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inSize,
            byte[] outBuf, uint outSize, out uint ret, IntPtr ov);

        const uint FSCTL_LOCK_VOLUME = 0x00090018;
        const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
        const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
        const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
        const uint IOCTL_DISK_GET_PARTITION_INFO_EX = 0x00070048;
        const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        const uint IOCTL_DISK_SET_PARTITION_INFO = 0x0007C008;

        static bool Ioctl(SafeFileHandle h, uint code, byte[] outBuf = null)
        {
            uint ret;
            return DeviceIoControl(h, code, null, 0, outBuf, outBuf == null ? 0 : (uint)outBuf.Length, out ret, IntPtr.Zero);
        }

        public static void Format(char letter, string label, int clusterBytes, bool quick, IProgress<int> progress)
        {
            using (var h = CreateFile($@"\\.\{letter}:", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid)
                    throw new IOException("Could not open the drive. Error " + Marshal.GetLastWin32Error());

                if (!Ioctl(h, FSCTL_LOCK_VOLUME))
                    throw new IOException("Could not lock the drive. Close anything using it and try again.");
                Ioctl(h, FSCTL_DISMOUNT_VOLUME);

                var geo = new byte[24];
                if (!Ioctl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY, geo))
                    throw new IOException("Could not read drive geometry.");
                int bps = BitConverter.ToInt32(geo, 20);
                if (bps < 512) bps = 512;

                var len = new byte[8];
                if (!Ioctl(h, IOCTL_DISK_GET_LENGTH_INFO, len))
                    throw new IOException("Could not read drive size.");
                long totalBytes = BitConverter.ToInt64(len, 0);
                long total = totalBytes / bps;

                long hidden = 0;
                var pi = new byte[144];
                if (Ioctl(h, IOCTL_DISK_GET_PARTITION_INFO_EX, pi))
                    hidden = BitConverter.ToInt64(pi, 8) / bps;

                // Pick cluster size
                if (clusterBytes <= 0)
                {
                    double gb = totalBytes / 1073741824.0;
                    clusterBytes = gb <= 8 ? 4096 : gb <= 16 ? 8192 : gb <= 32 ? 16384 : 32768;
                }
                int spc = Math.Max(1, clusterBytes / bps);
                const int reserved = 32;
                const int numFats = 2;
                int epc = bps / 4;

                long tmp1 = total - reserved;
                long tmp2 = (long)epc * spc + 1;
                long fatSz = (tmp1 + tmp2 - 1) / tmp2;
                long clusters = (total - reserved - numFats * fatSz) / spc;
                if (clusters < 65525)
                    throw new IOException("Drive is too small for FAT32 with this cluster size. Pick a smaller cluster size.");
                if (clusters > 0x0FFFFFF4)
                    throw new IOException("Too many clusters. Pick a larger cluster size.");

                long firstData = reserved + numFats * fatSz;

                using (var fs = new FileStream(h, FileAccess.ReadWrite, 1))
                {
                    // 1) Zero the area (whole drive for full format, metadata only for quick)
                    long zeroCount = quick ? firstData + spc : total;
                    ZeroRange(fs, bps, 0, Math.Min(zeroCount, total), progress);

                    // 2) Boot sector
                    var boot = new byte[bps];
                    boot[0] = 0xEB; boot[1] = 0x58; boot[2] = 0x90;
                    Put(boot, 3, System.Text.Encoding.ASCII.GetBytes("MSWIN4.1"));
                    BitConverter.GetBytes((ushort)bps).CopyTo(boot, 11);
                    boot[13] = (byte)spc;
                    BitConverter.GetBytes((ushort)reserved).CopyTo(boot, 14);
                    boot[16] = numFats;
                    boot[21] = 0xF8;
                    BitConverter.GetBytes((ushort)63).CopyTo(boot, 24);
                    BitConverter.GetBytes((ushort)255).CopyTo(boot, 26);
                    BitConverter.GetBytes((uint)hidden).CopyTo(boot, 28);
                    BitConverter.GetBytes((uint)total).CopyTo(boot, 32);
                    BitConverter.GetBytes((uint)fatSz).CopyTo(boot, 36);
                    BitConverter.GetBytes((uint)2).CopyTo(boot, 44);   // root cluster
                    BitConverter.GetBytes((ushort)1).CopyTo(boot, 48); // FSInfo sector
                    BitConverter.GetBytes((ushort)6).CopyTo(boot, 50); // backup boot sector
                    boot[64] = 0x80;
                    boot[66] = 0x29;
                    uint volId = (uint)Environment.TickCount ^ (uint)DateTime.Now.Ticks;
                    BitConverter.GetBytes(volId).CopyTo(boot, 67);
                    string lab = (label ?? "").ToUpperInvariant();
                    if (lab.Length > 11) lab = lab.Substring(0, 11);
                    string labPadded = lab.Length == 0 ? "NO NAME    " : lab.PadRight(11);
                    Put(boot, 71, System.Text.Encoding.ASCII.GetBytes(labPadded));
                    Put(boot, 82, System.Text.Encoding.ASCII.GetBytes("FAT32   "));
                    boot[bps - 2] = 0x55; boot[bps - 1] = 0xAA;

                    // 3) FSInfo
                    var info = new byte[bps];
                    BitConverter.GetBytes(0x41615252u).CopyTo(info, 0);
                    BitConverter.GetBytes(0x61417272u).CopyTo(info, 484);
                    BitConverter.GetBytes((uint)(clusters - 1)).CopyTo(info, 488);
                    BitConverter.GetBytes((uint)3).CopyTo(info, 492);
                    info[bps - 4] = 0; info[bps - 3] = 0; info[bps - 2] = 0x55; info[bps - 1] = 0xAA;

                    WriteSector(fs, bps, 0, boot);
                    WriteSector(fs, bps, 1, info);
                    WriteSector(fs, bps, 6, boot);
                    WriteSector(fs, bps, 7, info);

                    // 4) FATs
                    var fat = new byte[bps];
                    BitConverter.GetBytes(0x0FFFFFF8u).CopyTo(fat, 0);
                    BitConverter.GetBytes(0x0FFFFFFFu).CopyTo(fat, 4);
                    BitConverter.GetBytes(0x0FFFFFFFu).CopyTo(fat, 8);
                    WriteSector(fs, bps, reserved, fat);
                    WriteSector(fs, bps, reserved + fatSz, fat);

                    // 5) Root directory volume label entry
                    if (lab.Length > 0)
                    {
                        var root = new byte[bps];
                        Put(root, 0, System.Text.Encoding.ASCII.GetBytes(labPadded));
                        root[11] = 0x08;
                        WriteSector(fs, bps, firstData, root);
                    }

                    fs.Flush();

                    // 6) Mark the MBR partition as FAT32 LBA (0x0C) so the Wii recognizes it.
                    //    Fails harmlessly on GPT disks / drives with no partition table.
                    uint r2;
                    DeviceIoControl(h, IOCTL_DISK_SET_PARTITION_INFO, new byte[] { 0x0C }, 1, null, 0, out r2, IntPtr.Zero);

                    Ioctl(h, FSCTL_UNLOCK_VOLUME);
                }
            }
        }

        static void Put(byte[] dst, int off, byte[] src) { Array.Copy(src, 0, dst, off, src.Length); }

        static void WriteSector(FileStream fs, int bps, long sector, byte[] data)
        {
            fs.Seek(sector * bps, SeekOrigin.Begin);
            fs.Write(data, 0, data.Length);
        }

        static void ZeroRange(FileStream fs, int bps, long start, long count, IProgress<int> progress)
        {
            int chunkSectors = (1024 * 1024) / bps;
            var zeros = new byte[chunkSectors * bps];
            long done = 0;
            fs.Seek(start * bps, SeekOrigin.Begin);
            while (done < count)
            {
                long n = Math.Min(chunkSectors, count - done);
                fs.Write(zeros, 0, (int)(n * bps));
                done += n;
                progress?.Report((int)(done * 100 / count));
            }
        }
    }

    // ---------- GUI ----------
    class MainForm : Form
    {
        ComboBox drives = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        ComboBox cluster = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        TextBox label = new TextBox { MaxLength = 11 };
        CheckBox quick = new CheckBox { Text = "Quick format", Checked = true };
        CheckBox showFixed = new CheckBox { Text = "Show non-removable drives (not C:)" };
        ProgressBar bar = new ProgressBar();
        Button start = new Button { Text = "Format to FAT32" };
        Button refresh = new Button { Text = "Refresh" };
        Label status = new Label { Text = "Ready", AutoSize = true };

        public MainForm()
        {
            Text = "WiiFormat";
            try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(380, 290);
            StartPosition = FormStartPosition.CenterScreen;

            int y = 15;
            Add("Drive", drives, ref y, 270);
            refresh.SetBounds(290, 12, 75, 27);
            Controls.Add(refresh);
            Add("Cluster size", cluster, ref y, 270);
            Add("Volume label", label, ref y, 270);

            cluster.Items.AddRange(new object[] { "Default", "4096", "8192", "16384", "32768", "65536" });
            cluster.SelectedIndex = 0;

            quick.SetBounds(15, y, 300, 22); Controls.Add(quick); y += 28;
            showFixed.SetBounds(15, y, 340, 22); Controls.Add(showFixed); y += 35;
            bar.SetBounds(15, y, 350, 22); Controls.Add(bar); y += 32;
            status.Location = new Point(15, y); Controls.Add(status); y += 30;
            start.SetBounds(15, y, 350, 34); Controls.Add(start);

            refresh.Click += (s, e) => LoadDrives();
            showFixed.CheckedChanged += (s, e) => LoadDrives();
            start.Click += async (s, e) => await DoFormat();
            LoadDrives();
        }

        void Add(string text, Control c, ref int y, int width)
        {
            var l = new Label { Text = text, AutoSize = true, Location = new Point(15, y + 4) };
            Controls.Add(l);
            c.SetBounds(100, y, width - 85, 24);
            Controls.Add(c);
            y += 35;
        }

        void LoadDrives()
        {
            drives.Items.Clear();
            string sys = Path.GetPathRoot(Environment.SystemDirectory).Substring(0, 1).ToUpper();
            foreach (var d in DriveInfo.GetDrives())
            {
                string letter = d.Name.Substring(0, 1).ToUpper();
                if (letter == sys) continue;
                bool ok = d.DriveType == DriveType.Removable || (showFixed.Checked && d.DriveType == DriveType.Fixed);
                if (!ok) continue;
                string desc = letter + ":";
                try { if (d.IsReady) desc += $"  {d.VolumeLabel}  ({d.TotalSize / 1073741824.0:0.0} GB, {d.DriveFormat})"; }
                catch { }
                drives.Items.Add(desc);
            }
            if (drives.Items.Count > 0) drives.SelectedIndex = 0;
        }

        bool Confirm(char letter)
        {
            string info = $"Drive {letter}:";
            try
            {
                var d = new DriveInfo(letter.ToString());
                if (d.IsReady)
                {
                    string lab = d.VolumeLabel.Length == 0 ? "(none)" : d.VolumeLabel;
                    info = $"Drive:  {letter}:\n" +
                           $"Label:  {lab}\n" +
                           $"Size:  {d.TotalSize / 1073741824.0:0.0} GB\n" +
                           $"Used:  {(d.TotalSize - d.TotalFreeSpace) / 1073741824.0:0.0} GB\n" +
                           $"Current format:  {d.DriveFormat}";
                }
            }
            catch { }

            using (var f = new Form())
            {
                f.Text = "Confirm format";
                f.ClientSize = new Size(380, 300);
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false;

                var warn = new Label
                {
                    Text = "WARNING: everything on this drive will be permanently erased.",
                    ForeColor = Color.Firebrick,
                    Font = new Font(Font, FontStyle.Bold),
                    AutoSize = false
                };
                warn.SetBounds(15, 15, 350, 40);

                var details = new Label { Text = info, AutoSize = false, BorderStyle = BorderStyle.FixedSingle };
                details.SetBounds(15, 65, 350, 100);

                var hint = new Label { Text = $"Check this is the right drive. Type {letter} below to continue:", AutoSize = false };
                hint.SetBounds(15, 180, 350, 20);

                var box = new TextBox { MaxLength = 1 };
                box.SetBounds(15, 205, 40, 24);

                var ok = new Button { Text = "Format drive", Enabled = false, DialogResult = DialogResult.OK };
                ok.SetBounds(145, 250, 110, 32);
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
                cancel.SetBounds(265, 250, 100, 32);

                box.TextChanged += (s, e) =>
                    ok.Enabled = string.Equals(box.Text.Trim(), letter.ToString(), StringComparison.OrdinalIgnoreCase);

                f.Controls.AddRange(new Control[] { warn, details, hint, box, ok, cancel });
                f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK;
            }
        }

        async Task DoFormat()
        {
            if (drives.SelectedItem == null) { MessageBox.Show("No drive selected."); return; }
            string sel = drives.SelectedItem.ToString();
            char letter = sel[0];

            if (!Confirm(letter)) return;

            int cb = cluster.SelectedIndex == 0 ? 0 : int.Parse(cluster.SelectedItem.ToString());
            string lab = label.Text;
            bool q = quick.Checked;

            start.Enabled = false; refresh.Enabled = false;
            status.Text = "Formatting...";
            bar.Value = 0;
            var prog = new Progress<int>(p => bar.Value = Math.Min(100, p));
            try
            {
                await Task.Run(() => Fat32.Format(letter, lab, cb, q, prog));
                bar.Value = 100;
                status.Text = "Done.";
                MessageBox.Show("Format complete.", "WiiFormat");
            }
            catch (Exception ex)
            {
                status.Text = "Failed.";
                MessageBox.Show(ex.Message, "Format failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                start.Enabled = true; refresh.Enabled = true;
                LoadDrives();
            }
        }
    }
}
