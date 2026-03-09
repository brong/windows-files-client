#!/usr/bin/env python3
"""Mount a JMAP FileNode account as a FUSE filesystem.

Usage:
    mount_filenode.py --token TOKEN --session-url URL MOUNTPOINT
    mount_filenode.py --token TOKEN --session-url URL --debug MOUNTPOINT
"""

import argparse
import logging
import os
import subprocess

import pyfuse3
import trio

from jmap_client import JmapClient
from fusefs import FileNodeFS

log = logging.getLogger("mount")


async def main_async(args):
    # Connect to JMAP
    jmap = JmapClient(args.session_url, args.token)
    await jmap.connect()

    # Fetch the file tree
    home_id, trash_id = await jmap.find_home_and_trash()
    log.info("Home node: %s, Trash node: %s", home_id, trash_id)

    nodes, state = await jmap.get_all_nodes()
    log.info("Fetched %d nodes, state=%s", len(nodes), state)

    # Build the FUSE filesystem
    fs = FileNodeFS(jmap)
    fs.populate(nodes, home_id, trash_id)

    # Mount
    fuse_options = set(pyfuse3.default_options)
    fuse_options.add("fsname=filenode")
    if os.getuid() == 0:
        fuse_options.add("allow_other")
    if args.debug:
        fuse_options.add("debug")

    mountpoint = os.path.abspath(args.mountpoint)
    # Clean up stale FUSE mount if present
    if os.path.ismount(mountpoint):
        log.info("Unmounting stale FUSE mount at %s", mountpoint)
        subprocess.run(["fusermount3", "-u", mountpoint], check=False)
    os.makedirs(mountpoint, exist_ok=True)

    pyfuse3.init(fs, mountpoint, fuse_options)
    log.info("Mounted at %s (%d files)", mountpoint,
             sum(1 for n in nodes if not n.is_folder))

    try:
        await pyfuse3.main()
    finally:
        pyfuse3.close()
        await jmap.close()
        log.info("Unmounted.")


def main():
    parser = argparse.ArgumentParser(description="Mount JMAP FileNode as FUSE")
    parser.add_argument("mountpoint", help="Directory to mount on")
    parser.add_argument("--token", help="JMAP bearer token")
    parser.add_argument("--token-file", help="File containing JMAP bearer token")
    parser.add_argument("--session-url", required=True, help="JMAP session URL")
    parser.add_argument("--debug", action="store_true", help="Enable debug logging")
    args = parser.parse_args()

    # Resolve token
    if args.token_file:
        with open(args.token_file) as f:
            args.token = f.read().strip()
    if not args.token:
        parser.error("Either --token or --token-file is required")

    level = logging.DEBUG if args.debug else logging.INFO
    logging.basicConfig(
        level=level,
        format="%(asctime)s %(name)-6s %(levelname)-5s %(message)s",
        datefmt="%H:%M:%S",
    )
    # Suppress noisy third-party loggers even in debug mode
    logging.getLogger("httpcore").setLevel(logging.WARNING)
    logging.getLogger("httpx").setLevel(logging.WARNING)
    logging.getLogger("hpack").setLevel(logging.WARNING)

    # pyfuse3 requires trio as its event loop
    trio.run(main_async, args)


if __name__ == "__main__":
    main()
