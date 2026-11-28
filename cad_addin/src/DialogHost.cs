using System.Collections.Generic;
using System.Windows.Forms;

namespace HvacAiCadAddin
{
    /// <summary>
    /// Shows a command's modal dialog. Outside test mode this is exactly Application.ShowModalDialog. In test mode
    /// the dialog is built as usual (so it computes its settings in its own OK or Apply handler, with its own
    /// validation), and its accept button is clicked as soon as it is shown. A dialog whose accept button stays
    /// disabled (SizingForm with a missing capacity) or that does not close in time is cancelled and logged.
    /// </summary>
    internal static class DialogHost
    {
        private const int MaxTicks = 60;      // 60 x 100 ms
        private const int ClickTick = 3;      // give a dialog that recalculates on load time to enable its button

        internal static DialogResult ShowModal(Form form)
        {
            if (!TestMode.Enabled)
                return ShowInCad(form);

            int ticks = 0;
            bool clicked = false;
            Timer timer = new Timer { Interval = 100 };
            timer.Tick += (sender, args) =>
            {
                ticks++;
                if (ticks < ClickTick)
                    return;

                Button accept = FindAccept(form);
                if (!clicked && accept != null && accept.Enabled)
                {
                    clicked = true;
                    TestMode.Log("dialog \"" + form.Text + "\": accepted with defaults");
                    accept.PerformClick();
                    return;
                }

                if (ticks >= (clicked ? MaxTicks : ClickTick + 10))
                {
                    TestMode.Log("dialog \"" + form.Text + "\": "
                        + (clicked ? "did not close after OK" : "accept button missing or disabled") + ", cancelled");
                    timer.Stop();
                    form.DialogResult = DialogResult.Cancel;
                }
            };

            form.Shown += (sender, args) => timer.Start();
            try
            {
                // Plain WinForms: no owner window and no AcMgd type, so it also works in accoreconsole.
                return form.ShowDialog();
            }
            finally
            {
                timer.Stop();
                timer.Dispose();
            }
        }

        // The only place that names the CAD application's own dialog service (kept out of ShowModal so that a
        // host without it, accoreconsole, can still run the test path).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static DialogResult ShowInCad(Form form)
        {
            return Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form);
        }

        private static Button FindAccept(Form form)
        {
            Button button = form.AcceptButton as Button;
            if (button != null)
                return button;

            Queue<Control> queue = new Queue<Control>();
            queue.Enqueue(form);
            while (queue.Count > 0)
            {
                Control control = queue.Dequeue();
                Button candidate = control as Button;
                if (candidate != null && (candidate.Text == "Apply" || candidate.Text == "OK"))
                    return candidate;
                foreach (Control child in control.Controls)
                    queue.Enqueue(child);
            }

            return null;
        }
    }
}
