## Hello World

### run

```
bun run dev
```

### build

```
bun run build
```

### preview

```
bun run preview
```


### latest?

`bun run archive` should generate and move the latest `vite-plugin-fable.x.y.z.tgz` into `/archive` folder, at this point you can run: `bun i` to install latest version of the library and test it out. if you want to cleanup `node_modules` and lock files, and instal the dep from scratch a utility script is provided `install-arch` as well.
