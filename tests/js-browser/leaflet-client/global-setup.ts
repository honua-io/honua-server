import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { bootstrapHonuaServer } from '../../shared/js-bootstrap.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const projectRoot = resolve(__dirname, '..', '..', '..');

export default async function globalSetup() {
  const { teardown } = await bootstrapHonuaServer({
    defaultPort: '5556',
    label: 'browser tests',
    projectRoot,
  });
  return teardown;
}
