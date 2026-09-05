import {chromium} from '@playwright/test';
const browser=await chromium.connectOverCDP('http://127.0.0.1:18474');
const page=browser.contexts()[0].pages().find(p=>p.url().startsWith('https://app.boshan.local'));
if(!page)throw new Error('Native WebView2 page unavailable');
await page.getByRole('button',{name:'使用微软正版账号登录'}).waitFor();
if(await page.evaluate(()=>Object.keys(localStorage).length))throw new Error('Unexpected web storage');
await page.screenshot({path:'../.local/native-login.png',animations:'disabled'});
console.log(JSON.stringify({nativeWebView2:true,title:await page.title(),localStorageEmpty:true,loginForm:true}));
await browser.close();
