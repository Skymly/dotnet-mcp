"""Bridge worker-local loopback to the single inference socket mounted by the controller."""

import select
import socket
import socketserver
import subprocess
import sys
import threading


class Relay(socketserver.BaseRequestHandler):
    def handle(self):
        with socket.socket(socket.AF_UNIX) as upstream:
            upstream.connect("/run/inference.sock")
            peers = {self.request: upstream, upstream: self.request}
            while peers:
                for reader in select.select(list(peers), [], [])[0]:
                    data = reader.recv(65536)
                    if data:
                        peers[reader].sendall(data)
                    else:
                        peers.pop(reader).shutdown(socket.SHUT_WR)


if __name__ == "__main__":
    with socketserver.ThreadingTCPServer(("127.0.0.1", 17563), Relay) as relay:
        relay.daemon_threads = True
        threading.Thread(target=relay.serve_forever, daemon=True).start()
        sys.exit(subprocess.run(sys.argv[1:]).returncode)
