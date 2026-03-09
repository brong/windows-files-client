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


SYNC_INTERVAL = 30  # seconds between change polls


async def sync_loop(jmap, fs):
    """Background task: poll FileNode/changes and apply to the FUSE tree."""
    while True:
        await trio.sleep(SYNC_INTERVAL)
        if not jmap.state:
            continue
        try:
            while True:
                created, updated, destroyed, new_state, has_more = \
                    await jmap.get_changes(jmap.state)
                if not created and not updated and not destroyed:
                    jmap.state = new_state
                    break
                await fs.apply_changes(created, updated, destroyed)
                jmap.state = new_state
                if not has_more:
                    break
        except RuntimeError as e:
            if "cannotCalculateChanges" in str(e):
                log.warning("State too old, doing full resync")
                nodes, state = await jmap.get_all_nodes()
                home_id, trash_id = await jmap.find_home_and_trash()
                fs.populate(nodes, home_id, trash_id)
            else:
                log.error("Sync error: %s", e)
        except Exception as e:
            log.error("Sync error: %s", e)


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
    cache_dir = args.cache_dir or os.path.expanduser("~/.cache/filenode")
    fs = FileNodeFS(jmap, cache_dir=cache_dir)
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
        async with trio.open_nursery() as nursery:
            nursery.start_soon(sync_loop, jmap, fs)
            await pyfuse3.main()
            nursery.cancel_scope.cancel()
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
    parser.add_argument("--cache-dir", help="Directory for blob disk cache (default: ~/.cache/filenode)")
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
