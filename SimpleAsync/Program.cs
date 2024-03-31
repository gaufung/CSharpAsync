using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

GetTasks().Wait();

static async Task GetTasks()
{
    for (int i = 0; ; i++)
    {
        await MyTask.Delay(1000);
        Console.WriteLine(i);
    }
}

Console.ReadLine();

// Console.Write("Hello");
// MyTask.Delay(2000).ContinueWith(delegate {
//     Console.Write(", World!");
//     return MyTask.Delay(2000);
    
// }).ContinueWith(delegate {
//     Console.Write(" How Are you");
// }).Wait();

// AsyncLocal<int> asyncLocal = new AsyncLocal<int>();
// List<MyTask> tasks = new();
// for (int i = 0; i < 10; i++)
// {
//     asyncLocal.Value = i;
//     tasks.Add(MyTask.Run(() => {
//         Console.WriteLine(asyncLocal.Value);
//         MyTask.Delay(1000);
//     }));
// }

// MyTask.WhenAll(tasks).Wait();


class MyTask 
{
    private bool _completed;

    private Exception? _exception;

    private Action? _continuation;

    private ExecutionContext? _context;
    
    public bool IsCompleted
    {
        get 
        {
            lock(this)
            {
                return _completed;    
            }
        }
    }

    public void SetResult()  => Complete(null);

    public void SetException(Exception exception) => Complete(exception);


    public struct Awaiter(MyTask t) : INotifyCompletion
    {
        public Awaiter GetAwaiter() => this;

        public bool IsCompleted => t.IsCompleted;

        public void OnCompleted(Action continuation)
        {
            t.ContinueWith(continuation);
        }

        public void GetResult() => t.Wait();
    }

    public Awaiter GetAwaiter() => new(this);


    public void Complete(Exception? exception)
    {
        lock(this)
        {
            if (_completed)
            {
                throw new InvalidOperationException();
            }
            _completed = true;
            _exception = exception;
            if (_continuation != null)
            {
                MyThreadPool.QueueUserWorkItem(delegate {
                    if (_context == null) 
                    {
                        _continuation();
                    }
                    else 
                    {
                        ExecutionContext.Run(_context, state => ((Action)state!)(), _continuation);
                    }
                });
            }
        }
    }

    public void Wait()
    {
        ManualResetEventSlim? mres = null;

        lock(this)
        {
            if (!_completed)
            {
                mres = new ManualResetEventSlim();
                ContinueWith(mres.Set);
            }
        }

        mres?.Wait();

        if (_exception is not null)
        {
            // ExceptionDispatchInfo.Throw(_exception);
            throw new AggregateException(_exception);
        }
    }

    public MyTask ContinueWith(Func<MyTask> action)
    {
        MyTask t = new();

        Action callback = () => 
        {
            try
            {
                MyTask next = action();
                next.ContinueWith(delegate 
                {
                    if (next._exception is not null)
                    {
                        t.SetException(next._exception);
                    }
                    else
                    {
                        t.SetResult();
                    }
                });
            }
            catch (Exception ex)
            {
                t.SetException(ex);
                return;
            }
        };

        lock(this)
        {
            if (_completed)
            {
                MyThreadPool.QueueUserWorkItem(callback);
            }
            else
            {
                _continuation = callback;
                _context = ExecutionContext.Capture();
            }
        }

        return t;
    }

    public MyTask ContinueWith(Action action)
    {
        MyTask t = new();

        Action callback = () => 
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                t.SetException(ex);
                return;
            }

            t.SetResult();
        };

        lock(this)
        {
            if (_completed)
            {
                MyThreadPool.QueueUserWorkItem(callback);
            }
            else
            {
                _continuation = callback;
                _context = ExecutionContext.Capture();
            }
        }

        return t;
    }

    public static MyTask Run(Action action)
    {
        MyTask t = new();
        MyThreadPool.QueueUserWorkItem(delegate {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                t.SetException(ex);
                return;
            }

            t.SetResult();
        });
        return t;
    }

    public static MyTask WhenAll(List<MyTask> tasks)
    {
        MyTask t = new();

        if (tasks.Count == 0)
        {
            t.SetResult();
        }
        else 
        {
            int remaining = tasks.Count;
            Action continuation = () => {
                if (Interlocked.Decrement(ref remaining) == 0)
                {
                    t.SetResult();
                }
            };

            foreach(var task in tasks)
            {
                task.ContinueWith(continuation);
            }
        }
        return t;
    }

    public static MyTask Delay(int timeout)
    {
        MyTask t = new();
        new Timer(_ => t.SetResult()).Change(timeout, -1);
        return t;
    }

    public static MyTask Iterate(IEnumerable<MyTask> tasks)
    {
        MyTask t = new();
        IEnumerator<MyTask> e = tasks.GetEnumerator();
        void MoveNext()
        {
            try
            {
                if (e.MoveNext())
                {
                    MyTask next = e.Current;
                    next.ContinueWith(MoveNext);
                }
            }
            catch(Exception ex)
            {
                t.SetException(ex);
                return;
            }

            t.SetResult();
        }

        MoveNext();
        return t;
    }
}


static class MyThreadPool
{
    private static readonly BlockingCollection<(Action, ExecutionContext?)> s_workItems = new();

    public static void QueueUserWorkItem(Action action)
    {
        s_workItems.Add((action, ExecutionContext.Capture()));
    }

    static MyThreadPool()
    {
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            new Thread(() => 
            {
                while(true) 
                {
                    var (workItem, executionContext) = s_workItems.Take();
                    if (executionContext == null)
                    {
                        workItem();
                    }
                    else
                    {
                        ExecutionContext.Run(executionContext, state => ((Action)state!)(), workItem);
                    }
                }
            })
            {
                IsBackground = true
            }.Start();
        }
    }
}