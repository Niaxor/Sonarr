import copy from 'copy-to-clipboard';
import React, { useCallback, useEffect, useState } from 'react';
import Icon from 'Components/Icon';
import Link from 'Components/Link/Link';
import StatusIndicator from 'Components/StatusIndicator';
import { icons, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './SeriesDetailsPath.module.css';

type CopyState = 'success' | 'error' | null;

interface SeriesDetailsPathProps {
  path: string;
}

function SeriesDetailsPath({ path }: SeriesDetailsPathProps) {
  const [state, setState] = useState<CopyState>(null);

  useEffect(() => {
    if (!state) {
      return;
    }

    const timeoutId = setTimeout(() => {
      setState(null);
    }, 3000);

    return () => {
      clearTimeout(timeoutId);
    };
  }, [state]);

  const handlePress = useCallback(async () => {
    try {
      if ('clipboard' in navigator) {
        await navigator.clipboard.writeText(path);
      } else {
        copy(path);
      }

      setState('success');
    } catch (e) {
      setState('error');
      console.error('Failed to copy to clipboard', e);
    }
  }, [path]);

  return (
    <Link
      className={styles.path}
      title={translate('CopyToClipboard')}
      onPress={handlePress}
    >
      {state ? (
        <StatusIndicator
          className={styles.icon}
          label={translate(
            state === 'error' ? 'CopyToClipboardError' : 'CopiedToClipboard'
          )}
          role={state === 'error' ? 'alert' : 'status'}
          aria-atomic={true}
        >
          <Icon
            name={state === 'error' ? icons.DANGER : icons.CHECK}
            kind={state === 'error' ? kinds.DANGER : kinds.SUCCESS}
            size={14}
            aria-hidden={true}
          />
        </StatusIndicator>
      ) : (
        <span className={styles.icon}>
          <Icon
            className={styles.folderIcon}
            name={icons.FOLDER}
            size={14}
            aria-hidden={true}
          />
          <Icon
            className={styles.copyIcon}
            name={icons.CLIPBOARD}
            size={14}
            aria-hidden={true}
          />
        </span>
      )}

      <span className={styles.text}>{path}</span>
    </Link>
  );
}

export default SeriesDetailsPath;
