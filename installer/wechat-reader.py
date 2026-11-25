"""Read contacts and recent sessions from an explicitly configured local source."""
import json
from pathlib import Path

import click

from wechat_cli.commands.contacts import contacts
from wechat_cli.commands.sessions import sessions
from wechat_cli.core.context import AppContext


@click.group()
@click.option("--config", required=True, type=click.Path(path_type=Path, exists=True, dir_okay=False))
@click.pass_context
def reader(ctx, config):
    """微信助手本机只读查询组件。"""
    try:
        settings = json.loads(config.read_text(encoding="utf-8"))
        if not isinstance(settings, dict) or not settings.get("db_dir"):
            raise ValueError("An explicit source is required.")
        ctx.obj = AppContext(str(config))
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise click.ClickException("本机微信读取配置不可用。") from error


reader.add_command(contacts)
reader.add_command(sessions)

if __name__ == "__main__":
    reader()
