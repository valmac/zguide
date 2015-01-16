﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

using ZeroMQ;

namespace ZeroMQ.Test
{
	static partial class Program
	{
		// Load-balancing broker in C#
		// Clients and workers are shown here in-process

		static int LBBroker_Clients = 10;
		static int LBBroker_Workers = 3;

		// Basic request-reply client using REQ socket
		static void LBBroker_Client(ZContext context, int i)
		{
			// Create a socket
			using (var client = ZSocket.Create(context, ZSocketType.REQ))
			{
				// Set a printable identity
				client.Identity = Encoding.UTF8.GetBytes("CLIENT" + i);

				// Connect
				client.Connect("inproc://frontend");

				// Send request
				using (var request = new ZMessage())
				{
					request.Add(ZFrame.Create("Hello"));

					client.SendMessage(request);
				}

				// Receive reply
				using (ZMessage reply = client.ReceiveMessage())
				{
					Console.WriteLine("CLIENT{0}: {1}", i, reply[0].ReadString());
				}
			}
		}

		// While this example runs in a single process, that is just to make
		// it easier to start and stop the example. Each thread has its own
		// context and conceptually acts as a separate process.
		// This is the worker task, using a REQ socket to do load-balancing.
		// Because s_send and s_recv can't handle 0MQ binary identities, we
		// set a printable text identity to allow routing.

		static void LBBroker_Worker(ZContext context, int i)
		{
			// Create socket
			using (var worker = ZSocket.Create(context, ZSocketType.REQ))
			{
				// Set a printable identity
				worker.Identity = Encoding.UTF8.GetBytes("WORKER" + i);

				// Connect
				worker.Connect("inproc://backend");

				// Tell broker we're ready for work
				using (var ready = ZFrame.Create("READY"))
				{
					worker.SendFrame(ready);
				}

				while (true)
				{

					// Get request
					using (ZMessage work = worker.ReceiveMessage())
					{
						string worker_id = work[0].ReadString();

						string workerText = work[2].ReadString();
						Console.WriteLine("WORKER{0}: {1}", i, workerText);

						// Send reply
						using (var commit = new ZMessage())
						{
							commit.Add(ZFrame.Create(worker_id));
							commit.Add(ZFrame.Create(string.Empty));
							commit.Add(ZFrame.Create("OK"));

							worker.SendMessage(commit);
						}
					}
				}
			}
		}

		// This is the main task. It starts the clients and workers, and then
		// routes requests between the two layers. Workers signal READY when
		// they start; after that we treat them as ready when they reply with
		// a response back to a client. The load-balancing data structure is
		// just a queue of next available workers.

		public static void LBBroker(IDictionary<string, string> dict, string[] args)
		{
			// Prepare our context and sockets
			using (var context = ZContext.Create())
			using (var frontend = ZSocket.Create(context, ZSocketType.ROUTER))
			using (var backend = ZSocket.Create(context, ZSocketType.ROUTER))
			{
				// Bind
				frontend.Bind("inproc://frontend");
				// Bind
				backend.Bind("inproc://backend");

				int clients = 0;
				for (; clients < LBBroker_Clients; ++clients)
				{
					int j = clients;
					new Thread(() => LBBroker_Client(context, j)).Start();
				}
				for (int i = 0; i < LBBroker_Workers; ++i)
				{
					int j = i;
					new Thread(() => LBBroker_Worker(context, j)).Start();
				}

				// Here is the main loop for the least-recently-used queue. It has two
				// sockets; a frontend for clients and a backend for workers. It polls
				// the backend in all cases, and polls the frontend only when there are
				// one or more workers ready. This is a neat way to use 0MQ's own queues
				// to hold messages we're not ready to process yet. When we get a client
				// reply, we pop the next available worker and send the request to it,
				// including the originating client identity. When a worker replies, we
				// requeue that worker and forward the reply to the original client
				// using the reply envelope.

				// Queue of available workers

				ZError error;
				ZMessage incoming;
				// int avaliable_workers = 0;
				var worker_queue = new List<string>();
				var pollers = new ZPollItem[]
				{
					ZPollItem.CreateReceiver(backend),
					ZPollItem.CreateReceiver(frontend)
				};

				while (true)
				{
					// Handle worker activity on backend
					if (pollers[0].PollIn(out incoming, out error, TimeSpan.FromMilliseconds(64)))
					{
						// incoming[0] is worker_id
						string worker_id = incoming[0].ReadString();
						// Queue worker identity for load-balancing
						worker_queue.Add(worker_id);

						// incoming[1] is empty

						// incoming[2] is READY or else client_id
						string client_id = incoming[2].ReadString();

						if (client_id != "READY")
						{
							// incoming[3] is empty

							// incoming[4] is reply
							string reply = incoming[4].ReadString();

							using (var outgoing = new ZMessage())
							{
								outgoing.Add(ZFrame.Create(client_id));
								outgoing.Add(ZFrame.Create(string.Empty));
								outgoing.Add(ZFrame.Create(reply));

								frontend.SendMessage(outgoing);
							}

							if (--clients == 0)
								break;
						}
					}
					if (worker_queue.Count > 0)
					{
						// Poll frontend only if we have available workers

						if (pollers[1].PollIn(out incoming, out error, TimeSpan.FromMilliseconds(64)))
						{
							// incoming[0] is client_id
							string client_id = incoming[0].ReadString();

							// incoming[1] is empty

							// incoming[2] is request
							string requestText = incoming[2].ReadString();

							using (var outgoing = new ZMessage())
							{
								outgoing.Add(ZFrame.Create(worker_queue[0]));
								outgoing.Add(ZFrame.Create(string.Empty));
								outgoing.Add(ZFrame.Create(client_id));
								outgoing.Add(ZFrame.Create(string.Empty));
								outgoing.Add(ZFrame.Create(requestText));

								backend.SendMessage(outgoing);
							}

							worker_queue.RemoveAt(0);
						}
					}
				}
			}
		}
	}
}