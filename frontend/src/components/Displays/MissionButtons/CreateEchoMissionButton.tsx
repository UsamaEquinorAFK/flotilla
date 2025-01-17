import { Button, Icon } from '@equinor/eds-core-react'
import { useLanguageContext } from 'components/Contexts/LanguageContext'
import { Icons } from 'utils/icons'
import { useRef } from 'react'
import { useInstallationContext } from 'components/Contexts/InstallationContext'
import styled from 'styled-components'

const StyledButton = styled(Button)`
    display: flex;
    align-items: center;
    gap: 8px;
    border-radius: 4px;
`

export const CreateEchoMissionButton = (): JSX.Element => {
    const { TranslateText } = useLanguageContext()
    const { installationCode } = useInstallationContext()
    const anchorRef = useRef<HTMLButtonElement>(null)
    const echoURL = 'https://echo.equinor.com/missionplanner?instCode='

    return (
        <StyledButton
            variant="outlined"
            onClick={() => {
                window.open(echoURL + installationCode)
            }}
            disabled={installationCode === ''}
            ref={anchorRef}
        >
            <Icon name={Icons.ExternalLink} size={16}></Icon>
            {TranslateText('Create new Echo mission')}
        </StyledButton>
    )
}
