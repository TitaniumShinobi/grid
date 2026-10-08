// AUTH authorized verifier upgrade 1e45e6f3467b70c66c0338d0c6c0f937e9a18f8db32fdae8efef0a4f8f613e2e
import {readFile,lstat,readdir} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import {fileURLToPath} from 'node:url';
const root=new URL('../',import.meta.url), head='1e45e6f3467b70c66c0338d0c6c0f937e9a18f8db32fdae8efef0a4f8f613e2e';
const canonical=v=>Array.isArray(v)?v.map(canonical):v&&typeof v==='object'?Object.fromEntries(Object.keys(v).sort().map(k=>[k,canonical(v[k])])):v;
const hash=v=>createHash('sha256').update(Buffer.isBuffer(v)?v:JSON.stringify(canonical(v))).digest('hex');
const fail=code=>{throw Object.assign(new Error(code),{code});};
async function read(relative){
 let u=root;
 for(const part of relative.split('/')){u=new URL(part,u);const s=await lstat(u);if(s.isSymbolicLink())fail('UNSAFE_UPGRADE_PATH');if(s.isDirectory())u=new URL(u.href+'/');}
 return readFile(u);
}
try{
 const e=JSON.parse(await read('.auth-kit/upgrades/history/'+head+'.json'));
 const {digest,...payload}=e;if(digest!==head||hash(payload)!==head)fail('UPGRADE_HISTORY_TAMPERED');
 const base='.auth-kit/upgrades/generations/'+head+'/';
 if(hash((await readdir(new URL(base,root))).sort())!==hash(Object.keys(e.files).sort()))fail('VERIFIER_OR_GATE_DRIFT');
 for(const [name,expected] of Object.entries(e.files)){
  if(!/^[a-zA-Z0-9._-]+[.]mjs$/.test(name)||hash(await read(base+name))!==expected)fail('VERIFIER_OR_GATE_DRIFT');
 }
 const {upgradeChain}=await import(new URL(base+'auth-verifier-upgrade-chain.mjs',root));
 await upgradeChain(fileURLToPath(root));
 const {runProductCli}=await import(new URL(base+'auth-product-contract.mjs',root));
 await runProductCli(process.argv.slice(2),root);
}catch(e){console.log(JSON.stringify({verification:'FAIL',durability_verification:'FAIL',error:e.code||'UPGRADE_INVALID'}));process.exitCode=2;}
