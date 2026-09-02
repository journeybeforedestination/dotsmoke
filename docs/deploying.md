# Deploying

The public instance is <https://dotsmoke.wastebook.dev>. Every merge to `main`
publishes an image and deploys it: one DigitalOcean droplet, kamal-proxy on :80 and
:443 with a Let's Encrypt certificate it renews itself, forwarding to the app.

`config/deploy.yml` is the whole configuration, and its comments say why each value
is what it is.

## By hand

The route for a rollback, or for watching a deploy that is behaving oddly. Kamal is
a Ruby gem rather than anything this repo builds:

```bash
gem install kamal -v 2.12.0
export KAMAL_REGISTRY_PASSWORD=$(gh auth token)
kamal deploy --skip-push --version=$(git rev-parse main)
```

`--skip-push` is what makes this work on an image Kamal did not build: it pulls the
tag CI pushed, so the bytes that were attested are the bytes that run.

## Rolling back

Reverting the commit and merging is the normal route, through the same gate as
everything else. The faster one:

```bash
kamal rollback <earlier-commit>
```

Images are tagged by commit and Kamal keeps recent containers on the host, so this
does not work for a commit whose image was pruned.

## Setting up a new server

Rare enough to be a procedure rather than a workflow step. Create the droplet,
point the `A` record at it, put the deploy key in its `authorized_keys`, then:

```bash
kamal server bootstrap
ssh root@<host> 'mkdir -p /var/lib/dotsmoke && chown 1654:1654 /var/lib/dotsmoke'
```

`bootstrap` installs Docker. The second line creates the volume that holds `app.db`
and the `keys/` ring, owned by the image's non-root user — the container cannot
write a freshly mounted directory otherwise.

Kamal waits for the new container's health check before stopping the old one, so
every deploy has a few seconds where two processes have the SQLite file open. This
is accepted rather than solved: one droplet, one file, migrations that add.

## Secrets

Two, and neither belongs to the app: an SSH key made for this droplet and nothing
else, and the `GITHUB_TOKEN` minted for the workflow run, which expires with it. So
the server holds no standing credential, and nothing the app itself holds is secret
— which is why `config/deploy.yml` carries its settings in the clear.

What that buys is a merge that reaches the internet unattended. What it costs:
write access to this repository is equivalent to root on that droplet, because any
workflow run here can read the key. Deploying by hand reverses that trade.
