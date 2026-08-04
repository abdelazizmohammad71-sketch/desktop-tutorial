using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ZX0ai.Models;
using Microsoft.UI.Dispatching;

namespace ZX0ai.ViewModels
{
    public sealed class ActivityTimelineViewModel
    {
        public ObservableCollection<ActivityEntry> Activities { get; } = new ObservableCollection<ActivityEntry>();

        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

        public ActivityTimelineViewModel()
        {
        }

        public void Append(ActivityEntry entry)
        {
            // Ensure add happens on UI thread
            if (_dispatcher is not null)
            {
                _dispatcher.TryEnqueue(() => Activities.Add(entry));
            }
            else
            {
                Activities.Add(entry);
            }
        }

        public void UpdateState(Guid id, ActivityState state, double? progress = null)
        {
            if (_dispatcher is not null)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    var item = Find(id);
                    if (item != null)
                    {
                        item.State = state;
                        if (progress.HasValue) item.Progress = progress.Value;
                    }
                });
            }
            else
            {
                var item = Find(id);
                if (item != null)
                {
                    item.State = state;
                    if (progress.HasValue) item.Progress = progress.Value;
                }
            }
        }

        public void AppendOutput(Guid id, string text)
        {
            if (_dispatcher is not null)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    var it = Find(id);
                    if (it != null)
                    {
                        it.Output += text;
                    }
                });
            }
            else
            {
                var it = Find(id);
                if (it != null) it.Output += text;
            }
        }

        private ActivityEntry? Find(Guid id)
        {
            foreach (var a in Activities)
            {
                if (a.Id == id) return a;
            }
            return null;
        }

        // Small demo sequence to show timeline behaviour
        public async Task StartDemoAsync()
        {
            var s1 = new ActivityEntry { Title = "🔍 Searching for class MarkdownView...", IconKey = "IconSearch", State = ActivityState.Running };
            Append(s1);
            await Task.Delay(450);
            UpdateState(s1.Id, ActivityState.Completed);

            var s2 = new ActivityEntry { Title = "📄 Reading MarkdownView.xaml.cs", IconKey = "IconRead", State = ActivityState.Running };
            Append(s2);
            await Task.Delay(600);
            UpdateState(s2.Id, ActivityState.Completed);

            var s3 = new ActivityEntry { Title = "🧠 Analyzing layout...", IconKey = "IconThink", State = ActivityState.Running };
            Append(s3);
            for (int i = 0; i <= 100; i += 20)
            {
                UpdateState(s3.Id, ActivityState.Running, i / 100.0);
                await Task.Delay(180);
            }
            UpdateState(s3.Id, ActivityState.Completed, 1.0);

            var s4 = new ActivityEntry { Title = "✏ Editing MarkdownView.xaml", IconKey = "IconEdit", State = ActivityState.Running };
            Append(s4);
            await Task.Delay(400);
            s4.Details = "+12\n-4";
            UpdateState(s4.Id, ActivityState.Completed);

            var s5 = new ActivityEntry { Title = "⚙ Rebuilding project...", IconKey = "IconBuild", State = ActivityState.Running };
            Append(s5);
            for (int i = 0; i <= 100; i += 10)
            {
                UpdateState(s5.Id, ActivityState.Running, i / 100.0);
                AppendOutput(s5.Id, $"Compiling... {i}%\n");
                await Task.Delay(120);
            }
            UpdateState(s5.Id, ActivityState.Completed, 1.0);
            AppendOutput(s5.Id, "Build succeeded.\n");

            var s6 = new ActivityEntry { Title = "✔ Layout issue fixed", IconKey = "IconSuccess", State = ActivityState.Completed };
            Append(s6);
        }
    }
}
