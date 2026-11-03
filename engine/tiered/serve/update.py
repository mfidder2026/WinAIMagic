"""The updater is not part of this product: new versions come with the installer."""
RESTART_EXIT = 76


class Updater:
    def __init__(self, current=None, root=None):
        self.current = current

    def busy(self):
        return False

    def check(self, force=False):
        return {"current": self.current, "latest": None}

    def start(self, svc):
        raise ValueError("updates come with the installer")
