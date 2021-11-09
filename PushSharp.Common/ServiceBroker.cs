using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PushSharp.Common
{
    public class ServiceBroker<TNotification> : IServiceBroker<TNotification> where TNotification : INotification
    {
        static ServiceBroker ()
        {
            ServicePointManager.DefaultConnectionLimit = 100;
            ServicePointManager.Expect100Continue = false;
        }

        public ServiceBroker (IServiceConnectionFactory<TNotification> connectionFactory, ILogger<ServiceBroker<TNotification>> logger)
        {
            ServiceConnectionFactory = connectionFactory;
            _logger = logger;
            lockWorkers = new object ();
            workers = new List<ServiceWorker<TNotification>> ();
            running = false;

            notifications = new BlockingCollection<TNotification> ();
            ScaleSize = 1;
            //AutoScale = true;
            //AutoScaleMaxSize = 20;
        }

        public event NotificationSuccessDelegate<TNotification> OnNotificationSucceeded;
        public event NotificationFailureDelegate<TNotification> OnNotificationFailed;

        //public bool AutoScale { get; set; }
        //public int AutoScaleMaxSize { get; set; }
        public int ScaleSize { get; private set; }

        public IServiceConnectionFactory<TNotification> ServiceConnectionFactory { get; set; }

        BlockingCollection<TNotification> notifications;
        List<ServiceWorker<TNotification>> workers;
        object lockWorkers;
        bool running;
        private readonly ILogger<ServiceBroker<TNotification>> _logger;

        public virtual void QueueNotification (TNotification notification)
        {
            notifications.Add (notification);
        }

        public IEnumerable<TNotification> TakeMany ()
        {
            return notifications.GetConsumingEnumerable ();
        }

        public bool IsCompleted {
            get { return notifications.IsCompleted; }
        }

        public void Start ()
        {
            if (running)
                return;

            running = true;
            ChangeScale (ScaleSize);
        }

        public void Stop (bool immediately = false)
        {
            if (!running)
                throw new OperationCanceledException ("ServiceBroker has already been signaled to Stop");

            running = false;

            notifications.CompleteAdding ();

            lock (lockWorkers) {
                // Kill all workers right away
                if (immediately)
                    workers.ForEach (sw => sw.Cancel ());
					
                var all = (from sw in workers
                                       select sw.WorkerTask).ToArray ();

                _logger.LogInformation ("Stopping: Waiting on Tasks");

                Task.WaitAll (all);

                _logger.LogInformation("Stopping: Done Waiting on Tasks");

                workers.Clear ();
            }
        }

        public void ChangeScale (int newScaleSize)
        {
            if (newScaleSize <= 0)
                throw new ArgumentOutOfRangeException ("newScaleSize", "Must be Greater than Zero");

            ScaleSize = newScaleSize;

            if (!running)
                return;

            lock (lockWorkers) {

                // Scale down
                while (workers.Count > ScaleSize) {
                    workers [0].Cancel ();
                    workers.RemoveAt (0);
                }

                // Scale up
                while (workers.Count < ScaleSize) {
                    var worker = new ServiceWorker<TNotification> (this, ServiceConnectionFactory.Create (), _logger);
                    workers.Add (worker);
                    worker.Start ();
                }

                _logger.LogDebug ("Scaled Changed to: " + workers.Count);
            }
        }

        public void RaiseNotificationSucceeded (TNotification notification)
        {
            var evt = OnNotificationSucceeded;
            if (evt != null)
                evt (notification);
        }

        public void RaiseNotificationFailed (TNotification notification, AggregateException exception)
        {
            var evt = OnNotificationFailed;
            if (evt != null)
                evt (notification, exception);
        }
    }

    class ServiceWorker<TNotification> where TNotification : INotification
    {
        private readonly ILogger _logger;

        public ServiceWorker (IServiceBroker<TNotification> broker, IServiceConnection<TNotification> connection, ILogger logger)
        {
            Broker = broker;
            Connection = connection;
            _logger = logger;
            CancelTokenSource = new CancellationTokenSource ();
        }

        public IServiceBroker<TNotification> Broker { get; private set; }

        public IServiceConnection<TNotification> Connection { get; private set; }

        public CancellationTokenSource CancelTokenSource { get; private set; }

        public Task WorkerTask { get; private set; }

        public void Start ()
        {
            WorkerTask = Task.Factory.StartNew (async delegate {
                while (!CancelTokenSource.IsCancellationRequested && !Broker.IsCompleted) {

                    try {
                       
                        var toSend = new List<Task> ();
                        foreach (var n in Broker.TakeMany ()) {
                            var t = Connection.Send (n);
                            // Keep the continuation
                            var cont = t.ContinueWith (ct => {
                                var cn = n;
                                var ex = t.Exception;

                                if (ex == null)
                                    Broker.RaiseNotificationSucceeded (cn);
                                else
                                    Broker.RaiseNotificationFailed (cn, ex);                                
                            });

                            // Let's wait for the continuation not the task itself
                            toSend.Add (cont);
                        }

                        if (toSend.Count <= 0)
                            continue;
                       
                        try {
                            _logger.LogInformation("Waiting on all tasks {0}", toSend.Count ());
                            await Task.WhenAll (toSend).ConfigureAwait (false);
                            _logger.LogInformation("All Tasks Finished");
                        } catch (Exception ex) {
                            _logger.LogInformation("Waiting on all tasks Failed: {0}", ex);

                        }
                        _logger.LogInformation("Passed WhenAll");

                    } catch (Exception ex) {
                        _logger.LogError (ex, "Broker.Take:");
                    }
                }

                if (CancelTokenSource.IsCancellationRequested)
                    _logger.LogInformation("Cancellation was requested");
                if (Broker.IsCompleted)
                    _logger.LogInformation("Broker IsCompleted");

                _logger.LogDebug ("Broker Task Ended");
            }, CancelTokenSource.Token, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap ();
				
            WorkerTask.ContinueWith (t => {
                var ex = t.Exception;
                if (ex != null)
                    _logger.LogError (ex, "ServiceWorker.WorkerTask Error");
            }, TaskContinuationOptions.OnlyOnFaulted);              
        }

        public void Cancel ()
        {
            CancelTokenSource.Cancel ();
        }
    }
}

